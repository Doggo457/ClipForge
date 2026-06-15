using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Fragment.Services;

/// <summary>
/// A connected display, in virtual-desktop pixel coordinates (the same space gdigrab's
/// -offset_x/-offset_y use). Index is a stable left-to-right ordering.
/// </summary>
public sealed record MonitorInfo(int Index, int X, int Y, int Width, int Height, bool IsPrimary)
{
    public string Display =>
        $"{(IsPrimary ? "Primary" : $"Monitor {Index + 1}")} — {Width}×{Height} @ ({X},{Y})";
}

/// <summary>
/// Enumerates connected monitors via the Win32 multi-monitor API so the user can choose exactly
/// which screen to record (and so capture stays within a hardware encoder's resolution limit).
/// </summary>
public static class MonitorEnumerator
{
    public static List<MonitorInfo> GetMonitors()
    {
        var raw = new List<(RECT rect, bool primary)>();

        MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                bool isPrimary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;
                raw.Add((info.rcMonitor, isPrimary));
            }
            return true; // continue enumeration
        };

        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        }
        catch
        {
            return new List<MonitorInfo>();
        }

        return raw
            .OrderBy(m => m.rect.Left)
            .ThenBy(m => m.rect.Top)
            .Select((m, i) => new MonitorInfo(
                i, m.rect.Left, m.rect.Top,
                m.rect.Right - m.rect.Left, m.rect.Bottom - m.rect.Top, m.primary))
            .ToList();
    }

    /// <summary>Returns the monitor at <paramref name="index"/>, or the primary, or null.</summary>
    public static MonitorInfo? GetByIndex(int index)
    {
        var monitors = GetMonitors();
        if (monitors.Count == 0)
        {
            return null;
        }

        return monitors.FirstOrDefault(m => m.Index == index)
               ?? monitors.FirstOrDefault(m => m.IsPrimary)
               ?? monitors[0];
    }

    // ----- Win32 -----
    private const uint MONITORINFOF_PRIMARY = 1;

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
}
