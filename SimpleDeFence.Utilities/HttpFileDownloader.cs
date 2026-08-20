using System;
using System.IO;
using System.Net.Http;

namespace SimpleDeFence.Utilities
{
    /// <summary>
    /// Synchronous file download over HttpClient, for the two callers that are themselves
    /// synchronous - the service's update fetch and the update-descriptor fetch, both of which run
    /// on their own threads and have no async context to flow into.
    ///
    /// Replaces WebClient, which is obsolete (SYSLIB0014). This uses HttpClient's genuinely
    /// synchronous Send/ReadAsStream rather than blocking on a Task, so there is no
    /// sync-over-async and nothing to deadlock. The UI has its own async path
    /// (SimpleDeFence.UI/Services/Updater.cs) and does not use this.
    ///
    /// One static HttpClient: creating one per call is the documented way to exhaust sockets,
    /// because the underlying handler's connections outlive the disposed client.
    /// </summary>
    public static class HttpFileDownloader
    {
        private static readonly HttpClient Client = new HttpClient
        {
            // WebClient had no timeout by default, which meant a hung server hung the caller
            // indefinitely. HttpClient's own default is 100s; stated explicitly so it is a decision
            // rather than an accident.
            Timeout = TimeSpan.FromSeconds(100),
        };

        /// <summary>Downloads to <paramref name="destinationPath"/>, overwriting it. Throws on a
        /// non-success status - the previous WebClient.DownloadFile did too, and both callers rely
        /// on that to avoid treating an error page as a payload.</summary>
        public static void DownloadFile(string url, string destinationPath, string? headerName = null, string? headerValue = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(headerName) && headerValue is not null)
                request.Headers.Add(headerName, headerValue);

            // ResponseHeadersRead so the body streams to disk instead of being buffered whole.
            using var response = Client.Send(request, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            using var source = response.Content.ReadAsStream();
            using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            source.CopyTo(destination);
        }
    }
}
