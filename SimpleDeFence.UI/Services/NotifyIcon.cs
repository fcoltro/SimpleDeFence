using System;
using System.Runtime.InteropServices;

namespace SimpleDeFence.UI.Services
{
    /// <summary>
    /// The notification-area icon, straight onto Shell_NotifyIcon.
    ///
    /// This replaces H.NotifyIcon.WinUI, which is what the tray was built on until the dependency
    /// was dropped. WinUI 3 has no NotifyIcon of its own - System.Windows.Forms.NotifyIcon has no
    /// counterpart - so something has to talk to the shell directly; this is that something, and it
    /// is deliberately no larger than the four things the tray actually needs: an icon, a tooltip,
    /// a balloon, and a callback when the user clicks.
    ///
    /// It does not own a window. Shell_NotifyIcon needs an HWND to send its callback message to,
    /// and a *message-only* window would not do: HWND_MESSAGE windows are excluded from broadcasts,
    /// and TaskbarCreated - the message that says Explorer restarted and every icon must be
    /// re-added - is a broadcast. So it borrows the main window's HWND through WindowHotkeys, which
    /// already owns that window's single WndProc subclass and documents why there can only be one.
    /// </summary>
    internal sealed class NotifyIcon : IDisposable
    {
        // WM_APP-based, per the Shell_NotifyIcon documentation: the range above WM_APP is reserved
        // for an application's own private messages, so it cannot collide with anything the window
        // already receives.
        private const uint WM_TRAY_CALLBACK = 0x8000 + 1; // WM_APP + 1

        private const uint WM_CONTEXTMENU = 0x007B;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint NIN_SELECT = 0x0400;          // WM_USER + 0
        private const uint NIN_KEYSELECT = 0x0401;       // WM_USER + 1

        private const uint NIM_ADD = 0x0;
        private const uint NIM_MODIFY = 0x1;
        private const uint NIM_DELETE = 0x2;
        private const uint NIM_SETVERSION = 0x4;

        private const uint NIF_MESSAGE = 0x01;
        private const uint NIF_ICON = 0x02;
        private const uint NIF_TIP = 0x04;
        private const uint NIF_INFO = 0x10;
        private const uint NIF_SHOWTIP = 0x80;

        private const uint NOTIFYICON_VERSION_4 = 4;

        private const uint IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x00000010;
        private const uint LR_DEFAULTCOLOR = 0x00000000;

        private const int SM_CXSMICON = 49;
        private const int SM_CYSMICON = 50;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATAW
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
            /// <summary>Union of uTimeout and uVersion. Only ever used here as uVersion, by the
            /// NIM_SETVERSION call - uTimeout has been ignored by the shell since Vista.</summary>
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint RegisterWindowMessageW(string lpString);

        private readonly IntPtr _hwnd;
        private readonly WindowHotkeys _messages;
        private readonly uint _taskbarCreatedMessage;

        private IntPtr _hIcon;
        private string _tooltip = string.Empty;
        private bool _added;
        private bool _disposed;

        /// <summary>Left click, or Enter/Space while the icon has keyboard focus.</summary>
        public event Action? Selected;

        /// <summary>Right click, or the keyboard context-menu key. Carries the screen point the
        /// shell says the menu should be anchored at.</summary>
        public event Action<int, int>? ContextMenuRequested;

        public NotifyIcon(IntPtr hwnd, WindowHotkeys messages)
        {
            _hwnd = hwnd;
            _messages = messages;

            _messages.RegisterMessageHandler(WM_TRAY_CALLBACK, OnCallback);

            // Explorer can restart - it is one of the few things a user is routinely told to do
            // when the shell misbehaves - and every notification icon dies with it. The shell
            // broadcasts this registered message afterwards so applications can put theirs back;
            // an app that ignores it simply loses its tray icon until the next launch.
            _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
            if (_taskbarCreatedMessage != 0)
                _messages.RegisterMessageHandler(_taskbarCreatedMessage, (_, _) => Readd());
        }

        /// <summary>Adds the icon to the notification area. Separate from the constructor because
        /// the caller sets the icon and tooltip first, so the icon never appears blank.</summary>
        public void Create()
        {
            if (_disposed || _added)
                return;

            var data = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
            if (!Shell_NotifyIconW(NIM_ADD, ref data))
                return;

            _added = true;

            // Opts into the modern callback shape: WM_CONTEXTMENU and NIN_SELECT instead of raw
            // mouse messages, and - the reason it matters here - an anchor point in wParam that is
            // correct on every DPI and taskbar position, where the old shape left the caller to
            // guess from GetCursorPos.
            var version = NewData(0);
            version.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIconW(NIM_SETVERSION, ref version);
        }

        /// <summary>Loads a .ico from disk and shows it. The previous HICON is destroyed only after
        /// the new one is handed to the shell, so the icon never blinks through a moment of having
        /// no image; a failed load leaves the current icon alone rather than clearing it.</summary>
        public void SetIcon(string icoPath)
        {
            if (_disposed)
                return;

            // The shell wants a small icon. Asking for the exact metric rather than passing 0,0
            // stops LoadImage picking whichever size happens to be first in the .ico file.
            var handle = LoadImageW(IntPtr.Zero, icoPath, IMAGE_ICON,
                GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON),
                LR_LOADFROMFILE | LR_DEFAULTCOLOR);

            if (handle == IntPtr.Zero)
                return;

            var previous = _hIcon;
            _hIcon = handle;

            if (_added)
            {
                // NIF_TIP | NIF_SHOWTIP ride along with the icon change even though only the icon
                // moved. Under version 4 the standard tooltip is opt-in per NIF_SHOWTIP, and the
                // flag is not documented as sticky across later NIM_MODIFY calls - so an icon-only
                // modify risks silently switching the tooltip off. UpdateModeIcon calls SetIcon on
                // every mode change and every theme flip, far more often than SetTooltip, so the
                // icon would end up with no hover text for the rest of the session. Resending the
                // tip is a string copy this code already holds.
                var data = NewData(NIF_ICON | NIF_TIP | NIF_SHOWTIP);
                Shell_NotifyIconW(NIM_MODIFY, ref data);
            }

            if (previous != IntPtr.Zero)
                DestroyIcon(previous);
        }

        public void SetTooltip(string text)
        {
            if (_disposed)
                return;

            // szTip is a fixed 128-wchar buffer including its terminator. Marshalling a longer
            // string into a ByValTStr field throws, so a long translation would take the tray icon
            // down rather than be truncated by the shell.
            _tooltip = text.Length > 127 ? text.Substring(0, 127) : text;

            if (_added)
            {
                var data = NewData(NIF_TIP | NIF_SHOWTIP);
                Shell_NotifyIconW(NIM_MODIFY, ref data);
            }
        }

        /// <summary>Shows a balloon, which on Windows 10 and later the shell renders as a toast.
        /// Both strings are truncated for the same reason as the tooltip.</summary>
        public void ShowNotification(string title, string message)
        {
            if (_disposed || !_added)
                return;

            var data = NewData(NIF_INFO);
            data.szInfoTitle = title.Length > 63 ? title.Substring(0, 63) : title;
            data.szInfo = message.Length > 255 ? message.Substring(0, 255) : message;
            data.dwInfoFlags = 0; // NIIF_NONE - no shield/warning glyph beside the text.
            Shell_NotifyIconW(NIM_MODIFY, ref data);
        }

        private NOTIFYICONDATAW NewData(uint flags) => new NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = flags,
            uCallbackMessage = WM_TRAY_CALLBACK,
            hIcon = _hIcon,
            szTip = _tooltip,
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };

        private void OnCallback(IntPtr wParam, IntPtr lParam)
        {
            if (_disposed)
                return;

            // Version 4 packing: the event is in the low word of lParam, the icon id in the high
            // word, and wParam carries the anchor point - x in the low word, y in the high word.
            var eventCode = (uint)(lParam.ToInt64() & 0xFFFF);
            var x = (short)(wParam.ToInt64() & 0xFFFF);
            var y = (short)((wParam.ToInt64() >> 16) & 0xFFFF);

            switch (eventCode)
            {
                case NIN_SELECT:
                case NIN_KEYSELECT:
                // Accepted as well as NIN_SELECT, so that a failed NIM_SETVERSION above - which
                // leaves the shell sending the classic mouse-message shape - still opens the window
                // instead of silently doing nothing. If both ever arrive for one click the window
                // is simply restored twice, which ShowFromTray is idempotent about.
                case WM_LBUTTONUP:
                    Selected?.Invoke();
                    break;

                case WM_CONTEXTMENU:
                    ContextMenuRequested?.Invoke(x, y);
                    break;
            }
        }

        /// <summary>Puts the icon back after an Explorer restart. The old NOTIFYICONDATA is gone
        /// with the old shell process, so this is an add rather than a modify.</summary>
        private void Readd()
        {
            if (_disposed)
                return;

            _added = false;
            Create();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_added)
            {
                var data = NewData(0);
                Shell_NotifyIconW(NIM_DELETE, ref data);
                _added = false;
            }

            if (_hIcon != IntPtr.Zero)
            {
                DestroyIcon(_hIcon);
                _hIcon = IntPtr.Zero;
            }

            // Hand the window's messages back. Today the only caller disposes WindowHotkeys in the
            // same breath, which tears the whole subclass down anyway - but this object registered
            // these two handlers and should not depend on someone else's teardown to undo it. Left
            // as it is, disposing a NotifyIcon while keeping the window alive would leave the shell
            // callback pointed at a deleted icon.
            _messages.UnregisterMessageHandler(WM_TRAY_CALLBACK);
            if (_taskbarCreatedMessage != 0)
                _messages.UnregisterMessageHandler(_taskbarCreatedMessage);
        }
    }
}
