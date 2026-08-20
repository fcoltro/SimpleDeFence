using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using SimpleDeFence.Utilities;
using Xunit;

namespace SimpleDeFence.Tests
{
    /// <summary>
    /// Exercises the WebClient replacement against a loopback HttpListener rather than a real URL.
    /// A test that reaches the internet is not testing this code: on any machine running a firewall
    /// - including the one this project is developed on - the test host's outbound traffic is
    /// blocked and the test hangs until it times out, which is exactly what happened when this was
    /// first written against a GitHub URL.
    /// </summary>
    public class HttpFileDownloaderTests : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly string _prefix;
        private readonly Thread _thread;
        private volatile bool _stop;

        public HttpFileDownloaderTests()
        {
            var port = GetFreePort();
            _prefix = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(_prefix);
            _listener.Start();

            _thread = new Thread(Serve) { IsBackground = true };
            _thread.Start();
        }

        private static int GetFreePort()
        {
            var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        private void Serve()
        {
            while (!_stop)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { return; }

                var path = ctx.Request.Url?.AbsolutePath ?? "/";
                if (path == "/payload")
                {
                    var body = Encoding.UTF8.GetBytes("firewall configuration payload");
                    ctx.Response.StatusCode = 200;
                    ctx.Response.OutputStream.Write(body, 0, body.Length);
                }
                else if (path == "/header-echo")
                {
                    var body = Encoding.UTF8.GetBytes(ctx.Request.Headers["TW-Version"] ?? "(absent)");
                    ctx.Response.StatusCode = 200;
                    ctx.Response.OutputStream.Write(body, 0, body.Length);
                }
                else
                {
                    // An error page with a body, which is the case that matters: WebClient and this
                    // replacement must both throw rather than write it to disk as if it were the file.
                    var body = Encoding.UTF8.GetBytes("<html>404 not found</html>");
                    ctx.Response.StatusCode = 404;
                    ctx.Response.OutputStream.Write(body, 0, body.Length);
                }
                ctx.Response.OutputStream.Close();
            }
        }

        public void Dispose()
        {
            _stop = true;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }

        private static string TempPath() =>
            Path.Combine(Path.GetTempPath(), "sdf-dl-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void Writes_the_response_body_to_the_destination()
        {
            var dest = TempPath();
            try
            {
                HttpFileDownloader.DownloadFile(_prefix + "payload", dest);
                Assert.Equal("firewall configuration payload", File.ReadAllText(dest));
            }
            finally { if (File.Exists(dest)) File.Delete(dest); }
        }

        [Fact]
        public void Sends_the_supplied_request_header()
        {
            // The update-descriptor fetch identifies itself with TW-Version; losing that header in
            // the move off WebClient would be silent.
            var dest = TempPath();
            try
            {
                HttpFileDownloader.DownloadFile(_prefix + "header-echo", dest, "TW-Version", "9.9.9");
                Assert.Equal("9.9.9", File.ReadAllText(dest));
            }
            finally { if (File.Exists(dest)) File.Delete(dest); }
        }

        [Fact]
        public void Throws_on_an_error_status_instead_of_saving_the_error_body()
        {
            var dest = TempPath();
            try
            {
                Assert.ThrowsAny<System.Net.Http.HttpRequestException>(
                    () => HttpFileDownloader.DownloadFile(_prefix + "missing", dest));
                Assert.False(File.Exists(dest) && File.ReadAllText(dest).Contains("404"),
                    "an error page must not be left on disk as the payload");
            }
            finally { if (File.Exists(dest)) File.Delete(dest); }
        }

        [Fact]
        public void Overwrites_an_existing_destination_file()
        {
            var dest = TempPath();
            try
            {
                File.WriteAllText(dest, "stale content from a previous download that was longer");
                HttpFileDownloader.DownloadFile(_prefix + "payload", dest);
                Assert.Equal("firewall configuration payload", File.ReadAllText(dest));
            }
            finally { if (File.Exists(dest)) File.Delete(dest); }
        }
    }
}
