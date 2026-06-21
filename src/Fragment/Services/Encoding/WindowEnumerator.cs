using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Fragment.Services.Encoding;

/// <summary>A top-level window that can be captured: its HWND, title, and owning process.</summary>
public readonly record struct CapturableWindow(IntPtr Handle, string Title, uint ProcessId, string ProcessName)
{
    public string Display => string.IsNullOrEmpty(ProcessName) ? Title : $"{Title}  —  {ProcessName}";
    public override string ToString() => Display; // friendly text in the picker even without DisplayMemberPath
}

/// <summary>
/// Enumerates capturable top-level windows (for the "pick a window" picker) and tracks the foreground
/// window (for "follow the active window"). Skips invisible, cloaked (UWP ghost / off-desktop), titleless,
/// and tiny tool windows so the list matches what a user would think of as "an app window".
/// </summary>
public static class WindowEnumerator
{
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h); // minimized
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int val, int size);

    private const int DWMWA_CLOAKED = 14;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>The currently focused top-level window (IntPtr.Zero if none).</summary>
    public static IntPtr Foreground() => GetForegroundWindow();

    public static bool IsValid(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindowVisible(hwnd);

    public static uint ProcessIdOf(IntPtr hwnd) { GetWindowThreadProcessId(hwnd, out uint pid); return pid; }

    public static string TitleOf(IntPtr hwnd)
    {
        int len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new StringBuilder(len + 1);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>All visible, user-facing top-level windows, most-recently-used first (z-order from EnumWindows).</summary>
    public static List<CapturableWindow> List()
    {
        var result = new List<CapturableWindow>();
        IntPtr shell = GetShellWindow();
        EnumWindows((hwnd, _) =>
        {
            if (hwnd == shell || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if ((GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) return true; // floating tool palettes
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            if (!GetWindowRect(hwnd, out var r) || r.Right - r.Left < 96 || r.Bottom - r.Top < 64) return true;
            string title = TitleOf(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return true;

            GetWindowThreadProcessId(hwnd, out uint pid);
            string proc = "";
            try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
            result.Add(new CapturableWindow(hwnd, title, pid, proc));
            return true;
        }, IntPtr.Zero);
        return result;
    }
}
