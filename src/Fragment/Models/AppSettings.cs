using System.Collections.Generic;
using System.Linq;

namespace Fragment.Models;

/// <summary>
/// Top-level, JSON-serialized application settings. Persisted to
/// %AppData%\Fragment\settings.json by SettingsService.
/// </summary>
public class AppSettings
{
    /// <summary>All recording profiles. Seeded with one default profile.</summary>
    public List<RecordingProfile> Profiles { get; set; } = new()
    {
        new RecordingProfile()
    };

    /// <summary>Name of the currently selected profile.</summary>
    public string ActiveProfileName { get; set; } = "Default";

    // ---- Replay buffer / clipping ----------------------------------------

    /// <summary>Whether the rolling replay buffer runs in the background.</summary>
    public bool ReplayBufferEnabled { get; set; } = true;

    /// <summary>How many seconds of footage the replay buffer retains.</summary>
    public int ReplayBufferSeconds { get; set; } = 120;

    /// <summary>Default length of a saved clip, in seconds.</summary>
    public int ClipLengthSeconds { get; set; } = 30;

    // ---- Hotkeys ----------------------------------------------------------

    /// <summary>Global hotkey to start/stop a full recording.</summary>
    public string RecordHotkey { get; set; } = "Ctrl+Alt+R";

    /// <summary>Global hotkey to save a clip from the replay buffer.</summary>
    public string ClipHotkey { get; set; } = "Ctrl+Alt+C";

    /// <summary>Global hotkey to toggle the replay buffer on/off.</summary>
    public string ReplayToggleHotkey { get; set; } = "Ctrl+Alt+B";

    // ---- Misc -------------------------------------------------------------

    /// <summary>Explicit path to ffmpeg.exe. Null = auto-locate.</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>Whether closing the window minimizes to the system tray.</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Whether a sound plays when a clip is saved.</summary>
    public bool PlaySoundOnClip { get; set; } = true;

    /// <summary>UI theme name.</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>
    /// Returns the profile matching <see cref="ActiveProfileName"/>, falling back
    /// to the first profile, and finally to a fresh default if the list is empty.
    /// </summary>
    public RecordingProfile ActiveProfile()
    {
        var match = Profiles.FirstOrDefault(p => p.Name == ActiveProfileName);
        if (match is not null)
        {
            return match;
        }

        return Profiles.FirstOrDefault() ?? new RecordingProfile();
    }

    /// <summary>Deep copy, including a fresh clone of each profile. Used so the Settings dialog can
    /// edit a throwaway copy and only commit it back to the live settings on Save.</summary>
    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.Profiles = Profiles.Select(p => p.Clone()).ToList();
        return copy;
    }

    /// <summary>Copies all values from <paramref name="other"/> into this instance (deep for profiles).</summary>
    public void CopyFrom(AppSettings other)
    {
        Profiles = other.Profiles.Select(p => p.Clone()).ToList();
        ActiveProfileName = other.ActiveProfileName;
        ReplayBufferEnabled = other.ReplayBufferEnabled;
        ReplayBufferSeconds = other.ReplayBufferSeconds;
        ClipLengthSeconds = other.ClipLengthSeconds;
        RecordHotkey = other.RecordHotkey;
        ClipHotkey = other.ClipHotkey;
        ReplayToggleHotkey = other.ReplayToggleHotkey;
        FfmpegPath = other.FfmpegPath;
        MinimizeToTray = other.MinimizeToTray;
        PlaySoundOnClip = other.PlaySoundOnClip;
        Theme = other.Theme;
    }
}
