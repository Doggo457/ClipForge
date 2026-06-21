using System;
using Fragment.Models;
using Fragment.Services; // MonitorEnumerator, WgcCapture.MonitorFromPoint

namespace Fragment.Services.Encoding;

/// <summary>
/// Resolves a <see cref="RecordingProfile"/> capture source into a concrete WGC capture target: a monitor
/// HMONITOR or a window HWND, whether to letterbox-scale into a fixed canvas, and whether to follow the
/// foreground window. Shared by the direct recorder and the replay buffer so both behave identically.
/// </summary>
public readonly struct CaptureTarget
{
    public IntPtr Handle { get; init; }        // HMONITOR (monitor/full-screen) or HWND (window/active)
    public bool IsWindow { get; init; }        // window capture letterbox-scales into the canvas
    public bool FollowActive { get; init; }    // re-target the foreground window as focus changes
    public int CanvasWidth { get; init; }      // fixed output size; 0 => use the source's first-frame size
    public int CanvasHeight { get; init; }
    public bool CaptureCursor { get; init; }

    public static CaptureTarget Resolve(RecordingProfile p)
    {
        bool cursor = p.CaptureCursor;
        switch (p.Source)
        {
            case CaptureSource.Window:
            {
                IntPtr hwnd = p.WindowHandle;
                if (!WindowEnumerator.IsValid(hwnd)) hwnd = WindowEnumerator.Foreground(); // picker stale → fall back
                return new CaptureTarget { Handle = hwnd, IsWindow = true, CaptureCursor = cursor };
            }
            case CaptureSource.ActiveWindow:
            {
                var (cw, ch) = PrimarySize(); // fixed canvas so any focused window scales into a constant size
                return new CaptureTarget
                {
                    Handle = WindowEnumerator.Foreground(), IsWindow = true, FollowActive = true,
                    CanvasWidth = cw, CanvasHeight = ch, CaptureCursor = cursor,
                };
            }
            case CaptureSource.Monitor:
            {
                var mon = MonitorEnumerator.GetByIndex(p.MonitorIndex);
                IntPtr hmon = mon is not null ? WgcCapture.MonitorFromPoint(mon.X + 1, mon.Y + 1) : WgcCapture.MonitorFromPoint(0, 0);
                return new CaptureTarget { Handle = hmon, IsWindow = false, CaptureCursor = cursor };
            }
            default: // FullScreen → primary monitor
                return new CaptureTarget { Handle = WgcCapture.MonitorFromPoint(0, 0), IsWindow = false, CaptureCursor = cursor };
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetSystemMetrics(int n);
    private static (int w, int h) PrimarySize()
    {
        int w = GetSystemMetrics(0), h = GetSystemMetrics(1); // SM_CXSCREEN / SM_CYSCREEN
        return (w > 0 ? w + (w & 1) : 1920, h > 0 ? h + (h & 1) : 1080);
    }
}
