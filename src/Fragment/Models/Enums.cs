namespace Fragment.Models;

/// <summary>
/// What part of the desktop is captured.
/// </summary>
public enum CaptureSource
{
    FullScreen,
    Monitor,
    Region,
    Window
}

/// <summary>
/// Output container / muxer for the recorded file.
/// </summary>
public enum OutputContainer
{
    Mp4,
    Mkv,
    Mov,
    WebM,
    Gif
}

/// <summary>
/// Video encoder (ffmpeg codec) used for encoding the captured frames.
/// </summary>
public enum VideoEncoder
{
    x264,
    x265,
    NVENC_H264,
    NVENC_HEVC,
    AMF_H264,
    QSV_H264,
    VP9,
    AV1
}

/// <summary>
/// Which audio streams are captured and mixed.
/// </summary>
public enum AudioMode
{
    None,
    SystemOnly,
    MicOnly,
    SystemAndMic
}

/// <summary>
/// Encoder speed/quality preset. Values intentionally match ffmpeg preset names
/// (lower-case) so they can be passed straight through to "-preset".
/// </summary>
public enum RatePreset
{
    ultrafast,
    veryfast,
    fast,
    medium,
    slow
}
