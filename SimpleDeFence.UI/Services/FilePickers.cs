using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SimpleDeFence.UI.Services
{
    /// <summary>
    /// Open and Save dialogs that work from an elevated window.
    ///
    /// The app runs elevated (app.manifest asks for highestAvailable, and only an elevated window
    /// may talk to the service). The old WinRT pickers, Windows.Storage.Pickers, refuse to open
    /// from an elevated process and fail with "Unspecified error (0x80004005)", which is what every
    /// Add > Executable file showed. The Windows App SDK's own pickers,
    /// Microsoft.Windows.Storage.Pickers, are built for this case and are tried first. The old
    /// ones stay as the fallback for an unelevated window, where they still work.
    ///
    /// Both return a path, or null when the user cancels. When neither picker can open, the second
    /// failure is thrown for the caller to report.
    /// </summary>
    internal static class FilePickers
    {
        public static async Task<string?> OpenFileAsync(params string[] extensions)
        {
            if (App.MainWindow is null)
                return null;

            try
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.MainWindow.AppWindow.Id);
                foreach (var extension in extensions)
                    picker.FileTypeFilter.Add(extension);
                var result = await picker.PickSingleFileAsync();
                return result?.Path;
            }
            catch (Exception)
            {
                // Fall through to the older picker.
            }

            var legacy = new global::Windows.Storage.Pickers.FileOpenPicker();
            foreach (var extension in extensions)
                legacy.FileTypeFilter.Add(extension);
            WinRT.Interop.InitializeWithWindow.Initialize(legacy, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var file = await legacy.PickSingleFileAsync();
            return file?.Path;
        }

        public static async Task<string?> SaveFileAsync(string typeName, string extension, string suggestedName)
        {
            if (App.MainWindow is null)
                return null;

            try
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(App.MainWindow.AppWindow.Id);
                picker.FileTypeChoices.Add(typeName, new List<string> { extension });
                picker.SuggestedFileName = suggestedName;
                var result = await picker.PickSaveFileAsync();
                return result?.Path;
            }
            catch (Exception)
            {
                // Fall through to the older picker.
            }

            var legacy = new global::Windows.Storage.Pickers.FileSavePicker();
            legacy.FileTypeChoices.Add(typeName, new List<string> { extension });
            legacy.SuggestedFileName = suggestedName;
            WinRT.Interop.InitializeWithWindow.Initialize(legacy, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var file = await legacy.PickSaveFileAsync();
            return file?.Path;
        }
    }
}
