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
        private bool _disposed;

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

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_HOTKEY && _callbacks.TryGetValue(wParam.ToInt32(), out var callback))
                callback();

            // Windows announces a theme switch as WM_SETTINGCHANGE carrying the string
            // "ImmersiveColorSet"; the same message is broadcast for unrelated settings, so the
            // lParam string is what distinguishes it. Marshalling that string on every
            // WM_SETTINGCHANGE is cheap - the message is rare and never on a hot path.
            if (msg == WM_SETTINGCHANGE && lParam != IntPtr.Zero &&
                Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
            {
                SystemColorsChanged?.Invoke();
            }

            return CallWindowProc(_previousWndProc, hWnd, msg, wParam, lParam);
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
