using System;
using System.IO;

namespace Fragment.Models;

/// <summary>
/// A single, named set of capture/encode/output settings. The user can keep
/// several profiles (e.g. "Gameplay 1080p60", "Tutorial 30fps") and switch
/// between them. All properties have sensible defaults so a freshly constructed
/// profile is immediately usable.
/// </summary>
public class RecordingProfile
{
    /// <summary>Friendly, user-visible name. Also used as the lookup key.</summary>
    public string Name { get; set; } = "Default";

    // ---- Capture ----------------------------------------------------------

    /// <summary>What region of the desktop to capture.</summary>
    public CaptureSource Source { get; set; } = CaptureSource.FullScreen;

    /// <summary>Zero-based monitor index when <see cref="Source"/> is Monitor.</summary>
    public int MonitorIndex { get; set; } = 0;

    /// <summary>Capture region (pixels) when <see cref="Source"/> is Region.</summary>
    public int RegionX { get; set; }
    public int RegionY { get; set; }
    public int RegionWidth { get; set; }
    public int RegionHeight { get; set; }

    /// <summary>Window title to capture when <see cref="Source"/> is Window.</summary>
    public string? WindowTitle { get; set; }

    // ---- Video ------------------------------------------------------------

    /// <summary>Target capture frame rate.</summary>
    public int Fps { get; set; } = 60;

    /// <summary>Video encoder to use.</summary>
    public VideoEncoder Encoder { get; set; } = VideoEncoder.x264;

    /// <summary>Target video bitrate in kilobits per second.</summary>
    public int VideoBitrateKbps { get; set; } = 12000;

    /// <summary>Encoder speed/quality preset.</summary>
    public RatePreset Preset { get; set; } = RatePreset.veryfast;

    /// <summary>Whether to draw the mouse cursor into the capture.</summary>
    public bool CaptureCursor { get; set; } = true;

    // ---- Output container / audio ----------------------------------------

    /// <summary>Output container/muxer.</summary>
    public OutputContainer Container { get; set; } = OutputContainer.Mp4;

    /// <summary>Which audio streams to capture.</summary>
    public AudioMode Audio { get; set; } = AudioMode.SystemAndMic;

    /// <summary>DirectShow device name for system/loopback audio.</summary>
    public string? SystemAudioDevice { get; set; }

    /// <summary>DirectShow device name for the microphone.</summary>
    public string? MicDevice { get; set; }

    /// <summary>Target audio bitrate in kilobits per second.</summary>
    public int AudioBitrateKbps { get; set; } = 160;

    // ---- Output destination ----------------------------------------------

    /// <summary>Folder recordings are written to. Defaults to Videos\Fragment.</summary>
    public string OutputFolder { get; set; } = GetDefaultOutputFolder();

    /// <summary>
    /// File name template. Supported tokens: {date}, {time}, {profile}.
    /// The extension is derived from <see cref="Container"/>.
    /// </summary>
    public string FileNameTemplate { get; set; } = "Fragment_{date}_{time}";

    /// <summary>Deep copy. All members are value types or immutable strings, so a memberwise clone suffices.</summary>
    public RecordingProfile Clone() => (RecordingProfile)MemberwiseClone();

    private static string GetDefaultOutputFolder()
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrEmpty(videos))
        {
            videos = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        return Path.Combine(videos, "Fragment");
    }
}
