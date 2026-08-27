using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Graphics;

namespace SimpleDeFence.UI.Services
{
    /// <summary>
    /// Opens the tray's WinUI MenuFlyout at a point on the screen.
    ///
    /// A flyout has to be shown at an element, and it renders into that element's XamlRoot. The
    /// tray has no element and no XamlRoot: the main window may be hidden - closing it to the tray
    /// is the normal case, and is exactly when the tray menu matters most - so the menu cannot
    /// borrow it. This is the missing piece: a one-pixel, transparent, always-on-top window parked
    /// at the anchor point, whose only job is to give the flyout a XamlRoot and a place to hang
    /// from. H.NotifyIcon called the same trick ContextMenuMode.SecondWindow.
    ///
    /// Why not a native TrackPopupMenu instead, which would need none of this: the tray menu is a
    /// real WinUI MenuFlyout with a submenu and two ToggleMenuFlyoutItem checkmarks, and it follows
    /// the app's own light/dark setting through App.ApplyShellStyling. A native HMENU would render
    /// none of that and would follow the system theme instead of the app's - undoing, for this menu,
    /// the theming work the flyouts just had done to them.
    ///
    /// The window is created once and reused; showing it is a move plus a Show, not a construction.
    /// </summary>
    internal sealed class TrayMenuHost : IDisposable
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_LAYERED = 0x00080000;
        private const uint LWA_ALPHA = 0x2;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private readonly Window _window;
        private readonly AppWindow _appWindow;
        private readonly Grid _anchor;
        private bool _disposed;

        public TrayMenuHost()
        {
            _anchor = new Grid();
            _window = new Window { Content = _anchor };

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
            _appWindow = AppWindow.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));

            // No border, no title bar, never in the taskbar or Alt+Tab. WS_EX_TOOLWINDOW is set
            // directly as well as IsShownInSwitchers: the AppWindow property covers the switchers,
            // the extended style is what keeps a stray one-pixel button out of the taskbar.
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsAlwaysOnTop = true;
                presenter.IsResizable = false;
                presenter.IsMinimizable = false;
                presenter.IsMaximizable = false;
            }
            _appWindow.IsShownInSwitchers = false;

            var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle | WS_EX_TOOLWINDOW | WS_EX_LAYERED));

            // Fully transparent. The window is one pixel, so this is belt and braces - but a
            // single opaque pixel sitting on top of everything at the corner of the screen is the
            // kind of artefact nobody would ever think to look for.
            SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);
        }

        /// <summary>
        /// Shows <paramref name="menu"/> anchored at a screen point, in physical pixels - which is
        /// what the shell hands over in the notification icon's callback, and what AppWindow.Move
        /// expects, so no DPI conversion enters into it.
        /// </summary>
        public void Show(MenuFlyout menu, int screenX, int screenY)
        {
            if (_disposed)
                return;

            // try/finally, not a bare sequence: the window is made visible, topmost and foreground
            // *before* the flyout is asked to open, and it is the flyout's Closed event that hides
            // it again. If ShowAt throws, Closed never fires, and what is left behind is an
            // always-on-top window holding the foreground that nothing can bring down - the user's
            // clicks land on an invisible one-pixel window at the corner of the screen and the only
            // way out is to kill the app. Hiding on the way out of a failure costs one wasted
            // right-click instead.
            try
            {
                _appWindow.MoveAndResize(new RectInt32(screenX, screenY, 1, 1));
                _appWindow.Show(activateWindow: true);

                // The shell gives foreground to whatever the user clicked, which was the taskbar.
                // Without this the host never becomes active, and a flyout that is not in the
                // foreground does not get the deactivation that dismisses it - the menu would stay
                // on screen after a click elsewhere.
                SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(_window));

                // The anchor is one pixel; every part of the menu falls outside it, and outside the
                // host window with it. Without this the flyout is clipped to that pixel.
                menu.ShouldConstrainToRootBounds = false;

                // Auto rather than a fixed side: the notification area is usually bottom-right, so
                // the menu wants to open upward and leftward, but the taskbar can be on any edge
                // and the anchor is wherever it happens to be. Auto lets WinUI pick the side that
                // fits on the monitor, which is the same decision the shell makes for its own tray
                // menus.
                menu.ShowAt(_anchor, new FlyoutShowOptions
                {
                    // global::, because this file's own namespace makes a bare "Windows.Foundation"
                    // resolve against SimpleDeFence.Windows first - the same qualification RulesPage
                    // needs for Windows.Storage.
                    Position = new global::Windows.Foundation.Point(0, 0),
                    Placement = FlyoutPlacementMode.Auto,
                    ShowMode = FlyoutShowMode.Standard,
                });
            }
            catch (Exception e)
            {
                System.Diagnostics.Debug.WriteLine($"Tray menu failed to open: {e}");
                Hide();
            }
        }

        /// <summary>Hides the host once the menu it was holding has closed. Wired by the caller to
        /// the flyout's Closed event rather than done here, because one host serves the flyout for
        /// the life of the process and only the caller knows when it is finished with.</summary>
        public void Hide()
        {
            if (!_disposed)
                _appWindow.Hide();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _window.Close();
        }
    }
}
