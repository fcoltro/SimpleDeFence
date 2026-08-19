using System;
using System.IO;
using System.Runtime.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace SimpleDeFence
{
    /// <summary>
    /// WinUI's own local, per-user preferences - kept deliberately separate from
    /// SimpleDeFence.Settings.ControllerSettings (the WinForms GUI's equivalent). See the Settings
    /// plan's "why this plan does NOT touch SimpleDeFence/Settings.cs" note for why: sharing that
    /// class would either drag a System.Windows.Forms/System.Drawing dependency into Core for
    /// fields WinUI has no use for, or - if WinUI wrote only a subset of fields back to the same
    /// file WinForms uses - silently discard whatever WinForms-only preferences were already
    /// there. Reconciling the two is deferred to the eventual net10 exe-merge migration.
    /// </summary>
    [DataContract(Namespace = "SimpleDeFence")]
    public sealed class ClientSettings : ISerializable<ClientSettings>
    {
        [DataMember(EmitDefaultValue = false)]
        public string UiTheme { get; set; } = "auto";

        [DataMember(EmitDefaultValue = false)]
        public string Language { get; set; } = "auto";

        [DataMember(EmitDefaultValue = false)]
        public bool AskForExceptionDetails { get; set; } = false;

        [DataMember(EmitDefaultValue = false)]
        public bool EnableGlobalHotkeys { get; set; } = true;

        /// <summary>How often the Connections page re-polls while its auto-refresh toggle is on.
        /// The page polls on a timer - nothing pushes connection changes to it - so this is a real
        /// trade-off between freshness and the cost of a full snapshot, and belongs to the user.
        /// Read through <see cref="ConnectionsAutoRefreshInterval"/>, which clamps it: a persisted
        /// 0 (or a hand-edited negative) would otherwise mean a timer that fires continuously.</summary>
        [DataMember(EmitDefaultValue = false)]
        public int ConnectionsAutoRefreshSeconds { get; set; } = DefaultAutoRefreshSeconds;

        public const int DefaultAutoRefreshSeconds = 5;
        public const int MinAutoRefreshSeconds = 1;
        public const int MaxAutoRefreshSeconds = 300;

        /// <summary>Off by default. A firewall quietly writing a record of every program that
        /// touches the network is not something to opt users into.</summary>
        [DataMember(EmitDefaultValue = false)]
        public bool ConnectionLogEnabled { get; set; } = false;

        /// <summary>Empty means "use the default under the user's local app data" - see
        /// <see cref="ResolvedConnectionLogPath"/>. Stored rather than always-derived so a user can
        /// point it at a folder they actually watch.</summary>
        [DataMember(EmitDefaultValue = false)]
        public string ConnectionLogPath { get; set; } = string.Empty;

        [DataMember(EmitDefaultValue = false)]
        public int ConnectionLogIntervalSeconds { get; set; } = DefaultLogIntervalSeconds;

        [DataMember(EmitDefaultValue = false)]
        public int ConnectionLogMaxFileSizeMb { get; set; } = DefaultLogMaxFileSizeMb;

        public const int DefaultLogIntervalSeconds = 10;
        public const int MinLogIntervalSeconds = 1;
        public const int MaxLogIntervalSeconds = 3600;
        public const int DefaultLogMaxFileSizeMb = 5;
        public const int MinLogMaxFileSizeMb = 1;
        public const int MaxLogMaxFileSizeMb = 1024;

        public TimeSpan ConnectionLogInterval =>
            TimeSpan.FromSeconds(Clamp(
                ConnectionLogIntervalSeconds == 0 ? DefaultLogIntervalSeconds : ConnectionLogIntervalSeconds,
                MinLogIntervalSeconds,
                MaxLogIntervalSeconds));

        /// <summary>The configured path, or the default one under LocalApplicationData. Never
        /// returns empty, so callers cannot end up writing to the process's working directory -
        /// which for an installed build is Program Files.</summary>
        public string ResolvedConnectionLogPath
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(ConnectionLogPath))
                    return ConnectionLogPath;

                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SimpleDeFence", "logs", "connections.csv");
            }
        }

        public TimeSpan ConnectionsAutoRefreshInterval =>
            TimeSpan.FromSeconds(Clamp(
                ConnectionsAutoRefreshSeconds == 0 ? DefaultAutoRefreshSeconds : ConnectionsAutoRefreshSeconds,
                MinAutoRefreshSeconds,
                MaxAutoRefreshSeconds));

        /// <summary>Math.Clamp does not exist on .NET Framework, and this project multi-targets
        /// net48 as well as net10 - so using it here compiled for one target and broke the other.
        /// Kept local rather than pulled from a polyfill package: it is three tokens of arithmetic
        /// and the two callers above are its only users.</summary>
        private static int Clamp(int value, int min, int max)
            => value < min ? min : (value > max ? max : value);

        private static string FilePath
        {
            get
            {
#if DEBUG
                var dir = Path.GetDirectoryName(System.Reflection.Assembly.GetEntryAssembly()!.Location)!;
#else
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SimpleDeFence");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
#endif
                return Path.Combine(dir, "UIConfig");
            }
        }

        public static ClientSettings Load()
        {
            try
            {
                return SerializationHelper.DeserializeFromFile(FilePath, new ClientSettings());
            }
            catch
            {
                // First run (no file yet) or a corrupt/unreadable file are both a normal state,
                // not an error - the same defensive convention ControllerSettings.Load() already
                // uses. A fresh default instance (theme "auto") is a safe, honest fallback.
                return new ClientSettings();
            }
        }

        public void Save()
        {
            try
            {
                SerializationHelper.SerializeToFile(this, FilePath);
            }
            catch
            {
                // Best-effort persistence, matching ControllerSettings.Save()'s existing
                // convention - a failed save (e.g. AppData briefly locked) must not crash the
                // Settings page or block the in-memory theme change from applying.
            }
        }

        public JsonTypeInfo<ClientSettings> GetJsonTypeInfo()
        {
            return SourceGenerationContext.Default.ClientSettings;
        }
    }
}
