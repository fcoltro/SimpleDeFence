using System.Security.Principal;

namespace SimpleDeFence.UI.Services
{
    /// <summary>
    /// Whether this process is running elevated.
    ///
    /// app.manifest requests highestAvailable, so the answer is not a constant: an administrator
    /// gets an elevated token, a standard user gets their ordinary one, and the app runs either
    /// way. It matters because the control pipe's DACL admits Administrators and SYSTEM only - an
    /// unelevated instance can start, but it cannot reach the service, and the difference between
    /// "the firewall service is not running" and "this window is not allowed to talk to it" is the
    /// whole of what the user needs to be told.
    ///
    /// SimpleDeFence's own Utils.RunningAsAdmin does the same thing, and is not reachable from
    /// here: it lives in the SimpleDeFence assembly, which references this one and not the reverse.
    /// </summary>
    internal static class Elevation
    {
        private static readonly bool _isElevated = Detect();

        /// <summary>Cached, because a process cannot gain or lose elevation while it runs, and this
        /// is read on every shell refresh.</summary>
        internal static bool IsElevated => _isElevated;

        private static bool Detect()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                // Cannot tell. Reporting "not elevated" is the recoverable side of being wrong: the
                // worst outcome is offering a way to elevate that was not needed, where the other
                // way round hides the only explanation the user would get.
                return false;
            }
        }
    }
}
