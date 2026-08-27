using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace SimpleDeFence.UI.Services
{
    /// <summary>
    /// Registers global (system-wide) hotkeys against a WinUI 3 window's HWND, by subclassing its
    /// WndProc to intercept WM_HOTKEY - the WinUI equivalent of what
    /// SimpleDeFence.Windows/Hotkey.cs does via WinForms' Application.AddMessageFilter, which has no
    /// WinUI counterpart. One instance per window; not thread-safe (all calls expected from the UI
    /// thread, matching how RegisterHotKey/UnregisterHotKey are themselves not thread-safe).
    /// </summary>
    public sealed class WindowHotkeys : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const int WM_SETTINGCHANGE = 0x001A;
        private const int GWLP_WNDPROC = -4;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private readonly IntPtr _hwnd;
        private readonly IntPtr _previousWndProc;
        private readonly WndProcDelegate _newWndProcDelegate; // kept alive: see field comment below
        private readonly Dictionary<int, Action> _callbacks = new();
        private readonly Dictionary<uint, Action<IntPtr, IntPtr>> _messageHandlers = new();
        private bool _disposed;

        /// <summary>The window's HWND, for callers that have to hand it to an API of their own -
        /// Shell_NotifyIcon above all, which needs a window to send its callback message to.</summary>
        public IntPtr Handle => _hwnd;

        /// <summary>
        /// Routes one window message to a handler, for the same reason SystemColorsChanged exists
        /// below: a window has exactly one WndProc and this class owns the subclass, so anything
        /// else that needs to see messages has to come through here rather than chain a second
        /// SetWindowLongPtr of its own.
        ///
        /// The handler is given wParam and lParam raw and does not get to suppress the message -
        /// everything still reaches the original WndProc afterwards. That is all the notification
        /// icon needs, and it keeps this from turning into a general message-filter framework.
        /// One handler per message; registering the same message twice replaces the first.
        /// </summary>
        public void RegisterMessageHandler(uint message, Action<IntPtr, IntPtr> handler)
            => _messageHandlers[message] = handler;

        /// <summary>Detaches a handler registered above, so a component that owns messages on this
        /// window can leave it as it found it. Harmless if the message was never registered.</summary>
        public void UnregisterMessageHandler(uint message)
            => _messageHandlers.Remove(message);

        /// <summary>Raised when the user changes the Windows colour mode, so anything painted
        /// outside the XAML tree - the tray icon above all - can re-pick its artwork.
        ///
        /// This lives on the hotkey class rather than in a theme watcher of its own because a
        /// window has exactly one WndProc, and this class already owns the subclass. A second
        /// SetWindowLongPtr against the same HWND would chain in an order neither class controls,
        /// and whichever disposed second would restore a stale pointer over the other's.
        ///
        /// Raised on the UI thread (WndProc runs on the thread that owns the window), so handlers
        /// may touch UI directly.</summary>
        public event Action? SystemColorsChanged;

        public WindowHotkeys(Window window)
        {
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

            // The delegate passed to SetWindowLongPtr must be kept alive for the window's lifetime -
            // if it were a lambda with no field reference, the GC could collect it while native code
            // still holds the function pointer, corrupting the window's message handling.
            _newWndProcDelegate = WndProc;
            var newWndProcPtr = Marshal.GetFunctionPointerForDelegate(_newWndProcDelegate);
            _previousWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, newWndProcPtr);
        }

        public void RegisterHotkey(int id, uint virtualKey, uint modifiers, Action callback)
        {
            if (!RegisterHotKey(_hwnd, id, modifiers, virtualKey))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

            _callbacks[id] = callback;
        }

        public void UnregisterHotkey(int id)
        {
            _callbacks.Remove(id);
            UnregisterHotKey(_hwnd, id);
        }

        /// <summary>
        /// The window's message pump. This is a *native* callback - user32 calls it through a
        /// function pointer, not through any managed call site - so an exception that escapes it
        /// does not unwind into a caller that could catch it: it crosses back into native code,
        /// where the runtime terminates the process.
        ///
        /// That is why every dispatch below is individually guarded. The three handlers reachable
        /// from here run app code of real size - a hotkey opens a picker dialog, the tray callback
        /// puts a window on screen and opens a flyout - and any of them throwing would take the
        /// whole app down mid-message. Worse, it would take it down *before* NotifyIcon.Dispose
        /// runs NIM_DELETE, leaving the notification-area icon orphaned: the ghost icon that only
        /// disappears when the user waves the mouse over it. A handler that throws now loses that
        /// one interaction and nothing else.
        ///
        /// The forwarding CallWindowProc at the end is deliberately outside the guards - it must
        /// run for every message whatever the handlers did, or WinUI's own window handling breaks.
        /// </summary>
        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_HOTKEY && _callbacks.TryGetValue(wParam.ToInt32(), out var callback))
                Dispatch(callback);

            // Windows announces a theme switch as WM_SETTINGCHANGE carrying the string
            // "ImmersiveColorSet"; the same message is broadcast for unrelated settings, so the
            // lParam string is what distinguishes it. Marshalling that string on every
            // WM_SETTINGCHANGE is cheap - the message is rare and never on a hot path.
            if (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero &&
                Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
            {
                Dispatch(() => SystemColorsChanged?.Invoke());
            }

            if (_messageHandlers.TryGetValue(msg, out var handler))
                Dispatch(() => handler(wParam, lParam));

            return CallWindowProc(_previousWndProc, hWnd, msg, wParam, lParam);
        }

        /// <summary>Runs one handler, swallowing anything it throws. See WndProc for why this
        /// cannot be allowed to propagate. Debug.WriteLine rather than a dialog: this runs inside
        /// the message pump, and putting UI up from here would re-enter it.</summary>
        private static void Dispatch(Action handler)
        {
            try
            {
                handler();
            }
            catch (Exception e)
            {
                System.Diagnostics.Debug.WriteLine($"Window message handler threw: {e}");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            foreach (var id in new List<int>(_callbacks.Keys))
                UnregisterHotKey(_hwnd, id);
            _callbacks.Clear();

            // Restore the original WndProc before this object (and its delegate) can be collected.
            SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _previousWndProc);
        }
    }
}
