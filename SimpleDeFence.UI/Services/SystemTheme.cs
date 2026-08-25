using Microsoft.Win32;

namespace SimpleDeFence.UI.Services
{
    /// <summary>
    /// Whether the Windows taskbar - and so the notification area the tray icon sits in - is
    /// currently light or dark.
    /// </summary>
    internal static class SystemTheme
    {
        private const string PersonalizeKey =
            @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        /// <summary>
        /// True when the taskbar is light, which is when the tray needs the dark artwork.
        ///
        /// Reads SystemUsesLightTheme, NOT AppsUseLightTheme (which is what Utils.AppsUseLightTheme
        /// reads, correctly, for window content). The two are independent settings: Windows lets a
        /// user run light apps on a dark taskbar and vice versa, so choosing a tray icon from the
        /// apps value gets it exactly backwards for anyone on a mixed setting.
        ///
        /// Defaults to light on any failure. That is the safer default of the two: the dark
        /// artwork it selects is legible against the light taskbar most machines ship with, whereas
        /// guessing dark would put white-on-white if the guess were wrong.
        /// </summary>
        public static bool TaskbarUsesLightTheme()
        {
            try
            {
                return Registry.GetValue(PersonalizeKey, "SystemUsesLightTheme", 1) is int v && v != 0;
            }
            catch
            {
                return true;
            }
        }
    }
}
