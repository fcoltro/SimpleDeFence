using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SimpleDeFence.Windows
{
    /// <summary>A visible top-level window with a non-empty title.</summary>
    public readonly struct WindowInfo
    {
        public WindowInfo(string title, uint processId)
        {
            Title = title;
            ProcessId = processId;
        }

        public string Title { get; }
        public uint ProcessId { get; }
    }

    /// <summary>EnumWindows interop for the window picker. Pure P/Invoke - net48 and net10 safe.</summary>
    public static class TopLevelWindows
    {
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLengthW(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        public static List<WindowInfo> EnumerateVisible()
        {
            var result = new List<WindowInfo>();
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd))
                    return true;

                int len = GetWindowTextLengthW(hWnd);
                if (len == 0)
                    return true;

                var sb = new StringBuilder(len + 1);
                GetWindowTextW(hWnd, sb, sb.Capacity);
                GetWindowThreadProcessId(hWnd, out var pid);
                result.Add(new WindowInfo(sb.ToString(), pid));
                return true;
            }, IntPtr.Zero);
            return result;
        }
    }
}