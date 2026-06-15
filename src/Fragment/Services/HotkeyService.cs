using System;
using System.Collections.Generic;

using Fragment.Utils;

namespace Fragment.Services;

/// <summary>
/// Registers and dispatches global hotkeys via the Win32 RegisterHotKey API.
/// The host window must forward its WndProc messages into <see cref="ProcessMessage"/>
/// (typically by adding a hook to its HwndSource).
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly List<int> _registeredIds = new();
    private IntPtr _windowHandle = IntPtr.Zero;
    private int _nextId = 1;
    private bool _disposed;

    /// <summary>Raised on the UI/message thread when a registered hotkey fires; carries the hotkey id.</summary>
    public event Action<int>? HotkeyPressed;

    public HotkeyService()
    {
    }

    /// <summary>
    /// Associates this service with the window that owns the message loop. Must be called before
    /// <see cref="Register"/>. The caller is responsible for routing WndProc into <see cref="ProcessMessage"/>.
    /// </summary>
    public void Initialize(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
            throw new ArgumentException("A valid window handle is required.", nameof(windowHandle));

        _windowHandle = windowHandle;
    }

    /// <summary>
    /// Registers a global hotkey for the given modifiers and virtual-key code.
    /// </summary>
    /// <returns>The hotkey id, echoed back through <see cref="HotkeyPressed"/> when triggered.</returns>
    public int Register(uint modifiers, uint vk)
    {
        if (_windowHandle == IntPtr.Zero)
            throw new InvalidOperationException("HotkeyService.Initialize must be called before registering hotkeys.");

        var id = _nextId++;

        // MOD_NOREPEAT prevents a held-down hotkey from auto-repeating into a storm of WM_HOTKEY
        // messages (which would rapidly start/stop recording).
        if (!NativeMethods.RegisterHotKey(_windowHandle, id, modifiers | NativeMethods.MOD_NOREPEAT, vk))
        {
            throw new InvalidOperationException(
                $"Failed to register hotkey (modifiers=0x{modifiers:X}, vk=0x{vk:X}). It may already be in use.");
        }

        _registeredIds.Add(id);
        return id;
    }

    /// <summary>Unregisters every hotkey this service has registered.</summary>
    public void UnregisterAll()
    {
        if (_windowHandle == IntPtr.Zero)
        {
            _registeredIds.Clear();
            return;
        }

        foreach (var id in _registeredIds)
        {
            NativeMethods.UnregisterHotKey(_windowHandle, id);
        }

        _registeredIds.Clear();
    }

    /// <summary>
    /// Inspects a window message. If it is a WM_HOTKEY for one of our ids, raises
    /// <see cref="HotkeyPressed"/> and returns true so the host can mark it handled.
    /// </summary>
    public bool ProcessMessage(int msg, IntPtr wParam)
    {
        if (msg != NativeMethods.WM_HOTKEY)
            return false;

        var id = wParam.ToInt32();
        if (!_registeredIds.Contains(id))
            return false;

        HotkeyPressed?.Invoke(id);
        return true;
    }

    /// <summary>
    /// Parses a human-readable hotkey string such as "Ctrl+Alt+R" or "Ctrl+Shift+F5" into the
    /// modifier flags and virtual-key code expected by RegisterHotKey.
    /// Supports the modifiers Ctrl/Control, Alt, Shift, Win, and keys A-Z and F1-F12.
    /// </summary>
    public static (uint mods, uint vk) ParseHotkey(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            throw new ArgumentException("Hotkey string must not be empty.", nameof(s));

        uint mods = 0;
        uint vk = 0;
        var sawKey = false;

        var tokens = s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var rawToken in tokens)
        {
            var token = rawToken.ToUpperInvariant();

            switch (token)
            {
                case "CTRL":
                case "CONTROL":
                    mods |= NativeMethods.MOD_CONTROL;
                    continue;
                case "ALT":
                    mods |= NativeMethods.MOD_ALT;
                    continue;
                case "SHIFT":
                    mods |= NativeMethods.MOD_SHIFT;
                    continue;
                case "WIN":
                case "WINDOWS":
                case "META":
                case "SUPER":
                    mods |= NativeMethods.MOD_WIN;
                    continue;
            }

            if (sawKey)
                throw new FormatException($"Hotkey '{s}' specifies more than one non-modifier key.");

            // A-Z map directly to their ASCII/virtual-key codes (0x41-0x5A).
            if (token.Length == 1 && token[0] >= 'A' && token[0] <= 'Z')
            {
                vk = token[0];
                sawKey = true;
                continue;
            }

            // F1-F12 -> VK_F1 (0x70) .. VK_F12 (0x7B).
            if (token.Length >= 2 && token[0] == 'F'
                && int.TryParse(token.AsSpan(1), out var fNum)
                && fNum is >= 1 and <= 12)
            {
                vk = (uint)(0x70 + (fNum - 1));
                sawKey = true;
                continue;
            }

            throw new FormatException($"Unsupported hotkey token '{rawToken}' in '{s}'.");
        }

        if (!sawKey)
            throw new FormatException($"Hotkey '{s}' does not contain a key (only modifiers).");

        return (mods, vk);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        UnregisterAll();
        HotkeyPressed = null;
        _disposed = true;
    }
}
