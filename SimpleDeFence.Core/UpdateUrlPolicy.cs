using System;

namespace SimpleDeFence
{
    /// <summary>
    /// Where an update payload may come from.
    ///
    /// Every payload URL arrives inside the update descriptor, an unsigned file fetched from the
    /// repository, and the hash next to it comes from the same file - so the hash proves the bytes
    /// are the ones the descriptor names, not that the descriptor is genuine. Pinning the payload to
    /// this project's own GitHub releases over HTTPS at least means a tampered or mistyped
    /// descriptor cannot point the updater at an arbitrary host, or at plain HTTP where anyone on
    /// the path can supply the bytes and the matching hash is already public.
    /// </summary>
    public static class UpdateUrlPolicy
    {
        // Release assets live on github.com; the database and hosts modules may be served
        // straight from the repository through raw.githubusercontent.com.
        private static readonly string[] AllowedHosts = { "github.com", "raw.githubusercontent.com" };
        private const string AllowedPathPrefix = "/fcoltro/SimpleDeFence/";

        public static bool IsAllowed(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return false;

            return uri.Scheme == Uri.UriSchemeHttps
                && uri.IsDefaultPort
                && string.IsNullOrEmpty(uri.UserInfo)
                && Array.Exists(AllowedHosts, h => string.Equals(uri.Host, h, StringComparison.OrdinalIgnoreCase))
                && uri.AbsolutePath.StartsWith(AllowedPathPrefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
