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

    // ----- Display mode (for matching capture fps to the monitor refresh rate) -----
    private const int ENUM_CURRENT_SETTINGS = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode);

    /// <summary>
    /// Returns the primary monitor's current refresh rate in Hz (e.g. 60, 144, 240). Falls back to
    /// 60 if the value can't be read or looks bogus. Used to default the capture frame rate so
    /// recordings match the display and don't judder.
    /// </summary>
    public static int GetPrimaryRefreshHz()
    {
        try
        {
            var dm = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettings(null, ENUM_CURRENT_SETTINGS, ref dm))
            {
                int hz = (int)dm.dmDisplayFrequency;
                if (hz is >= 24 and <= 1000)
                {
                    return hz;
                }
            }
        }
        catch
        {
            // fall through to the safe default
        }

        return 60;
    }
}
