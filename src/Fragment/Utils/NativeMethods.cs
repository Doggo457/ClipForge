using System.Runtime.InteropServices;

namespace Fragment.Utils;

/// <summary>
/// P/Invoke declarations for the Win32 global hotkey API and related constants.
/// </summary>
internal static class NativeMethods
{
    // ----- Modifier flags for RegisterHotKey (fsModifiers) -----
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000; // suppress auto-repeat while a hotkey is held

    // ----- Window message raised when a registered hotkey fires -----
    public const int WM_HOTKEY = 0x0312;

    /// <summary>
    /// Registers a system-wide hotkey. Returns false if the combination is
    /// already taken by another application.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    /// <summary>
    /// Releases a previously registered hotkey identified by <paramref name="id"/>.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ----- Screen metrics -----
    private const int SM_CXSCREEN = 0;   // primary monitor width (pixels)
    private const int SM_CYSCREEN = 1;   // primary monitor height (pixels)

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>
    /// Returns the primary monitor's pixel size. gdigrab "desktop" otherwise spans the entire
    /// multi-monitor virtual desktop, which can exceed a hardware encoder's max width (≈4096px)
    /// and is rarely what the user wants to record.
    /// </summary>
    public static (int Width, int Height) GetPrimaryScreenSize()
    {
        try
        {
            int w = GetSystemMetrics(SM_CXSCREEN);
            int h = GetSystemMetrics(SM_CYSCREEN);
            return (w, h);
        }
        catch
        {
            return (0, 0);
        }
    }
}
