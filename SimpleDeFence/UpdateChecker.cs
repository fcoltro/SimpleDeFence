using System;
using System.IO;
using System.Reflection;
using SimpleDeFence.Utilities;

namespace SimpleDeFence
{
    /// <summary>
    /// Fetches the update descriptor. Called by the service's periodic auto-update check
    /// (SimpleDeFenceService) and by the GUI's Services.Updater, which owns the user-facing half
    /// of updating - progress, prompts and installation - as WinUI ContentDialogs.
    ///
    /// This file used to also carry an <c>Updater</c> class that drove the same flow through
    /// native TaskDialogs. It was superseded by SimpleDeFence.UI/Services/Updater.cs during the
    /// WinUI migration and left behind unreferenced - nothing ever called its StartUpdate entry
    /// point - so it has been removed along with the vendored TaskDialog wrapper it was
    /// the last consumer of.
    /// </summary>
    internal static class UpdateChecker
    {
        /// <summary>Read from the assembly rather than WinForms' Application.ProductVersion, which
        /// was the last thing pulling System.Windows.Forms into this file. Same value: both report
        /// the informational version the build stamps in.</summary>
        private static string ProductVersion =>
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            ?? "unknown";
        private const string URL_UPDATE_DESCRIPTOR = @"https://raw.githubusercontent.com/fcoltro/SimpleDeFence/refs/heads/main/updates/update.json";

        internal static UpdateDescriptor GetDescriptor()
        {
            var tmpFile = Path.GetTempFileName();

            try
            {
                HttpFileDownloader.DownloadFile(URL_UPDATE_DESCRIPTOR, tmpFile, "TW-Version", ProductVersion);

                var descriptor = SerializationHelper.DeserializeFromFile(tmpFile, new UpdateDescriptor());
                if (descriptor.MagicWord != "SimpleDeFence Update Descriptor")
                    throw new ApplicationException("Bad update descriptor file.");

                return descriptor;
            }
            finally
            {
                File.Delete(tmpFile);
            }
        }
    }
}
