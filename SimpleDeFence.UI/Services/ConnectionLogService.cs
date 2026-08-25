using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SimpleDeFence.UI.Services
{
    /// <summary>
    /// Appends what the Connections screen sees - blocked attempts and established connections - to
    /// a CSV file, so there is a record of which software tried to reach the network while nobody
    /// was watching. Off by default; everything about it comes from <see cref="ClientSettings"/>.
    ///
    /// Owned by App rather than by ConnectionsPage on purpose. The page is the obvious place to
    /// hook, but it only refreshes while it is the visible page, so logging driven from there would
    /// quietly record nothing for the entire time the user is on Rules or Settings, or has the
    /// window closed to the tray - which is exactly when an unattended log is worth having.
    /// </summary>
    internal sealed class ConnectionLogService : IDisposable
    {
        private readonly DispatcherQueueTimer _timer;
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private ClientSettings _settings = new();
        private bool _polling;
        private bool _disposed;

        /// <summary>Why logging last failed to write, or null. Surfaced by the Settings page rather
        /// than thrown: a log that cannot be written is worth telling the user about once, but it
        /// must never take the firewall's GUI down with it, and a timer tick has nowhere to throw.</summary>
        public string? LastError { get; private set; }

        /// <summary>Bound on the live set of keys used to suppress duplicate rows. Established
        /// connections carry no timestamp, so they are de-duplicated by identity and would
        /// otherwise accumulate one entry per connection ever seen for the life of the process.
        /// Well above any realistic connection count, small enough to stay irrelevant to memory.</summary>
        private const int MaxSeenKeys = 20000;

        public ConnectionLogService(DispatcherQueue dispatcher)
        {
            _timer = dispatcher.CreateTimer();
            _timer.Tick += async (_, _) => await PollAsync();
            ApplySettings(ClientSettings.Load());
        }

        /// <summary>Re-reads the persisted settings and starts, stops or re-times the poll to match.
        /// Called at launch and by the Settings page the moment any logging value changes, so the
        /// user does not have to restart to see the toggle take effect.</summary>
        public void ApplySettings(ClientSettings settings)
        {
            if (_disposed)
                return;

            _settings = settings;
            _timer.Stop();

            if (!settings.ConnectionLogEnabled)
                return;

            LastError = null;
            _timer.Interval = settings.ConnectionLogInterval;
            _timer.Start();
        }

        private async System.Threading.Tasks.Task PollAsync()
        {
            // A slow gather must not pile ticks up behind it: the timer keeps firing while an
            // await is outstanding, and two concurrent writers would interleave half-lines.
            if (_polling || _disposed || !_settings.ConnectionLogEnabled)
                return;

            _polling = true;
            try
            {
                if (!App.Firewall.Connected)
                    return;

                var snapshot = await App.Firewall.GetConnectionsAsync();
                var lines = new List<string>();

                foreach (var row in snapshot.Blocked)
                {
                    // Blocked rows carry their own timestamp, so identity includes it and a repeat
                    // attempt by the same app to the same place is correctly logged as a new event.
                    var key = string.Create(CultureInfo.InvariantCulture,
                        $"B|{row.Timestamp.Ticks}|{row.ProcessId}|{row.AppName}|{row.Protocol}|{row.Direction}|{row.RemoteAddress}|{row.RemotePort}");
                    if (!_seen.Add(key))
                        continue;

                    lines.Add(Csv(row.Timestamp, "Blocked", row.AppName, row.ProcessId, row.Protocol,
                        row.Direction, string.Empty, Endpoint(row.RemoteAddress, row.RemotePort),
                        string.Empty, row.AppPath ?? string.Empty));
                }

                foreach (var row in snapshot.Connected)
                {
                    var key = string.Create(CultureInfo.InvariantCulture,
                        $"C|{row.ProcessId}|{row.AppName}|{row.Protocol}|{row.LocalAddress}|{row.LocalPort}|{row.RemoteAddress}|{row.RemotePort}|{row.State}");
                    if (!_seen.Add(key))
                        continue;

                    lines.Add(Csv(DateTime.Now, "Connected", row.AppName, row.ProcessId, row.Protocol,
                        "Out", Endpoint(row.LocalAddress, row.LocalPort),
                        Endpoint(row.RemoteAddress, row.RemotePort), row.State, row.AppPath));
                }

                // Cleared wholesale rather than pruned: the only thing the set is protecting
                // against is re-logging a connection that is still open, and the cost of clearing
                // is at worst one duplicate row per still-live connection, once.
                if (_seen.Count > MaxSeenKeys)
                    _seen.Clear();

                if (lines.Count > 0)
                    Append(lines);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
            finally
            {
                _polling = false;
            }
        }

        private void Append(List<string> lines)
        {
            var path = _settings.ResolvedConnectionLogPath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            Rotate(path);

            var writeHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
            var text = new StringBuilder();
            if (writeHeader)
                text.AppendLine("Timestamp,Event,Application,PID,Protocol,Direction,Local,Remote,State,Path");
            foreach (var line in lines)
                text.AppendLine(line);

            File.AppendAllText(path, text.ToString(), Encoding.UTF8);
            LastError = null;
        }

        /// <summary>Size-based rotation keeping exactly one previous file, rather than the "max
        /// time" a date-stamped scheme would give. A connection log grows at a rate nobody can
        /// predict from the clock - an idle machine writes nothing for hours, a busy one fills
        /// megabytes in minutes - so a size cap is what actually bounds the disk it can take, which
        /// is the thing worth bounding on a firewall that is meant to run unattended forever.</summary>
        private void Rotate(string path)
        {
            if (!File.Exists(path))
                return;

            var maxBytes = (long)_settings.ConnectionLogMaxFileSizeMb * 1024 * 1024;
            if (new FileInfo(path).Length < maxBytes)
                return;

            var previous = path + ".1";
            if (File.Exists(previous))
                File.Delete(previous);
            File.Move(path, previous);
        }

        private static string Endpoint(string address, int port)
            => string.IsNullOrEmpty(address) ? string.Empty
                                             : string.Create(CultureInfo.InvariantCulture, $"{address}:{port}");

        private static string Csv(DateTime timestamp, string kind, string app, uint pid, string protocol,
                                  string direction, string local, string remote, string state, string path)
        {
            var fields = new[]
            {
                timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                kind, app, pid.ToString(CultureInfo.InvariantCulture), protocol,
                direction, local, remote, state, path,
            };

            var sb = new StringBuilder();
            for (int i = 0; i < fields.Length; i++)
            {
                if (i > 0)
                    sb.Append(',');
                sb.Append(Quote(fields[i]));
            }
            return sb.ToString();
        }

        /// <summary>Everything is quoted rather than only the fields that need it. Application names
        /// and paths are attacker-influenced text on a security tool's log - a path containing a
        /// comma or a quote must not be able to shift the columns of every row after it.</summary>
        private static string Quote(string value)
            => "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _timer.Stop();
        }
    }
}
