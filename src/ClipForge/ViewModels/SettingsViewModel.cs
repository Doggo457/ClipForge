using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ClipForge.Models;
using ClipForge.Services;
using ClipForge.Utils;

namespace ClipForge.ViewModels;

/// <summary>
/// View model backing <c>SettingsWindow</c>. Wraps an <see cref="AppSettings"/>
/// instance (and its active <see cref="RecordingProfile"/>) and exposes the
/// individual fields as bindable properties grouped per tab:
/// Capture / Video / Audio / Clipping / Hotkeys / Output.
/// </summary>
public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly AppSettings _settings;
    private readonly RecordingProfile _profile;
    private DeviceEnumerator? _deviceEnumerator;
    private MonitorInfo? _selectedMonitor;

    public SettingsViewModel(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _profile = _settings.ActiveProfile();

        // Static enum-backed option lists (populated once).
        CaptureSources = new ObservableCollection<CaptureSource>(Enum.GetValues<CaptureSource>());
        Encoders = new ObservableCollection<VideoEncoder>(Enum.GetValues<VideoEncoder>());
        Containers = new ObservableCollection<OutputContainer>(Enum.GetValues<OutputContainer>());
        AudioModes = new ObservableCollection<AudioMode>(Enum.GetValues<AudioMode>());
        Presets = new ObservableCollection<RatePreset>(Enum.GetValues<RatePreset>());

        VideoDevices = new ObservableCollection<string>();
        AudioDevices = new ObservableCollection<string>();

        Monitors = new ObservableCollection<MonitorInfo>(MonitorEnumerator.GetMonitors());
        _selectedMonitor =
            Monitors.FirstOrDefault(m => m.Index == _profile.MonitorIndex)
            ?? Monitors.FirstOrDefault(m => m.IsPrimary)
            ?? Monitors.FirstOrDefault();

        BrowseOutputFolderCommand = new RelayCommand(_ => BrowseOutputFolder?.Invoke());
        RefreshDevicesCommand = new RelayCommand(async _ => await RefreshDevicesAsync());
        SaveCommand = new RelayCommand(_ => Save());

        // Attempt to wire up the device enumerator if FFmpeg is available.
        var ffmpeg = FfmpegLocator.Find(_settings.FfmpegPath);
        if (FfmpegLocator.IsValid(ffmpeg))
        {
            _deviceEnumerator = new DeviceEnumerator(ffmpeg!);
            // Fire and forget: populate device lists in the background.
            _ = RefreshDevicesAsync();
        }
    }

    /// <summary>Raised when the folder-picker button is clicked so the View can show a dialog.</summary>
    public event Action? BrowseOutputFolder;

    // ----- Option lists -------------------------------------------------

    public ObservableCollection<CaptureSource> CaptureSources { get; }
    public ObservableCollection<VideoEncoder> Encoders { get; }
    public ObservableCollection<OutputContainer> Containers { get; }
    public ObservableCollection<AudioMode> AudioModes { get; }
    public ObservableCollection<RatePreset> Presets { get; }
    public ObservableCollection<string> VideoDevices { get; }
    public ObservableCollection<string> AudioDevices { get; }
    public ObservableCollection<MonitorInfo> Monitors { get; }

    // ----- Commands -----------------------------------------------------

    public ICommand BrowseOutputFolderCommand { get; }
    public ICommand RefreshDevicesCommand { get; }
    public ICommand SaveCommand { get; }

    // ===================================================================
    // Capture tab
    // ===================================================================

    public CaptureSource Source
    {
        get => _profile.Source;
        set { if (_profile.Source != value) { _profile.Source = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsRegion)); OnPropertyChanged(nameof(IsMonitor)); OnPropertyChanged(nameof(IsWindow)); } }
    }

    public bool IsRegion => _profile.Source == CaptureSource.Region;
    public bool IsMonitor => _profile.Source == CaptureSource.Monitor;
    public bool IsWindow => _profile.Source == CaptureSource.Window;

    public int MonitorIndex
    {
        get => _profile.MonitorIndex;
        set { if (_profile.MonitorIndex != value) { _profile.MonitorIndex = value; OnPropertyChanged(); } }
    }

    /// <summary>The monitor to record when <see cref="Source"/> is Monitor.</summary>
    public MonitorInfo? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            if (!ReferenceEquals(_selectedMonitor, value))
            {
                _selectedMonitor = value;
                if (value is not null)
                {
                    _profile.MonitorIndex = value.Index;
                    OnPropertyChanged(nameof(MonitorIndex));
                }
                OnPropertyChanged();
            }
        }
    }

    public int RegionX
    {
        get => _profile.RegionX;
        set { if (_profile.RegionX != value) { _profile.RegionX = value; OnPropertyChanged(); } }
    }

    public int RegionY
    {
        get => _profile.RegionY;
        set { if (_profile.RegionY != value) { _profile.RegionY = value; OnPropertyChanged(); } }
    }

    public int RegionWidth
    {
        get => _profile.RegionWidth;
        set { if (_profile.RegionWidth != value) { _profile.RegionWidth = value; OnPropertyChanged(); } }
    }

    public int RegionHeight
    {
        get => _profile.RegionHeight;
        set { if (_profile.RegionHeight != value) { _profile.RegionHeight = value; OnPropertyChanged(); } }
    }

    public string? WindowTitle
    {
        get => _profile.WindowTitle;
        set { if (_profile.WindowTitle != value) { _profile.WindowTitle = value; OnPropertyChanged(); } }
    }

    public bool CaptureCursor
    {
        get => _profile.CaptureCursor;
        set { if (_profile.CaptureCursor != value) { _profile.CaptureCursor = value; OnPropertyChanged(); } }
    }

    // ===================================================================
    // Video tab
    // ===================================================================

    public int Fps
    {
        get => _profile.Fps;
        set { if (_profile.Fps != value) { _profile.Fps = value; OnPropertyChanged(); } }
    }

    public VideoEncoder Encoder
    {
        get => _profile.Encoder;
        set { if (_profile.Encoder != value) { _profile.Encoder = value; OnPropertyChanged(); } }
    }

    public int VideoBitrateKbps
    {
        get => _profile.VideoBitrateKbps;
        set { if (_profile.VideoBitrateKbps != value) { _profile.VideoBitrateKbps = value; OnPropertyChanged(); } }
    }

    public RatePreset Preset
    {
        get => _profile.Preset;
        set { if (_profile.Preset != value) { _profile.Preset = value; OnPropertyChanged(); } }
    }

    public OutputContainer Container
    {
        get => _profile.Container;
        set { if (_profile.Container != value) { _profile.Container = value; OnPropertyChanged(); } }
    }

    // ===================================================================
    // Audio tab
    // ===================================================================

    public AudioMode Audio
    {
        get => _profile.Audio;
        set { if (_profile.Audio != value) { _profile.Audio = value; OnPropertyChanged(); OnPropertyChanged(nameof(UsesSystemAudio)); OnPropertyChanged(nameof(UsesMic)); } }
    }

    public bool UsesSystemAudio => _profile.Audio is AudioMode.SystemOnly or AudioMode.SystemAndMic;
    public bool UsesMic => _profile.Audio is AudioMode.MicOnly or AudioMode.SystemAndMic;

    public string? SystemAudioDevice
    {
        get => _profile.SystemAudioDevice;
        set { if (_profile.SystemAudioDevice != value) { _profile.SystemAudioDevice = value; OnPropertyChanged(); } }
    }

    public string? MicDevice
    {
        get => _profile.MicDevice;
        set { if (_profile.MicDevice != value) { _profile.MicDevice = value; OnPropertyChanged(); } }
    }

    public int AudioBitrateKbps
    {
        get => _profile.AudioBitrateKbps;
        set { if (_profile.AudioBitrateKbps != value) { _profile.AudioBitrateKbps = value; OnPropertyChanged(); } }
    }

    // ===================================================================
    // Clipping tab
    // ===================================================================

    public bool ReplayBufferEnabled
    {
        get => _settings.ReplayBufferEnabled;
        set { if (_settings.ReplayBufferEnabled != value) { _settings.ReplayBufferEnabled = value; OnPropertyChanged(); } }
    }

    public int ReplayBufferSeconds
    {
        get => _settings.ReplayBufferSeconds;
        set { if (_settings.ReplayBufferSeconds != value) { _settings.ReplayBufferSeconds = value; OnPropertyChanged(); } }
    }

    public int ClipLengthSeconds
    {
        get => _settings.ClipLengthSeconds;
        set { if (_settings.ClipLengthSeconds != value) { _settings.ClipLengthSeconds = value; OnPropertyChanged(); } }
    }

    public bool PlaySoundOnClip
    {
        get => _settings.PlaySoundOnClip;
        set { if (_settings.PlaySoundOnClip != value) { _settings.PlaySoundOnClip = value; OnPropertyChanged(); } }
    }

    // ===================================================================
    // Hotkeys tab
    // ===================================================================

    public string RecordHotkey
    {
        get => _settings.RecordHotkey;
        set { if (_settings.RecordHotkey != value) { _settings.RecordHotkey = value; OnPropertyChanged(); } }
    }

    public string ClipHotkey
    {
        get => _settings.ClipHotkey;
        set { if (_settings.ClipHotkey != value) { _settings.ClipHotkey = value; OnPropertyChanged(); } }
    }

    public string ReplayToggleHotkey
    {
        get => _settings.ReplayToggleHotkey;
        set { if (_settings.ReplayToggleHotkey != value) { _settings.ReplayToggleHotkey = value; OnPropertyChanged(); } }
    }

    // ===================================================================
    // Output tab
    // ===================================================================

    public string OutputFolder
    {
        get => _profile.OutputFolder;
        set { if (_profile.OutputFolder != value) { _profile.OutputFolder = value; OnPropertyChanged(); } }
    }

    public string FileNameTemplate
    {
        get => _profile.FileNameTemplate;
        set { if (_profile.FileNameTemplate != value) { _profile.FileNameTemplate = value; OnPropertyChanged(); } }
    }

    public string? FfmpegPath
    {
        get => _settings.FfmpegPath;
        set
        {
            if (_settings.FfmpegPath != value)
            {
                _settings.FfmpegPath = value;
                OnPropertyChanged();
                // Re-evaluate the device enumerator when the FFmpeg path changes.
                var ffmpeg = FfmpegLocator.Find(_settings.FfmpegPath);
                _deviceEnumerator = FfmpegLocator.IsValid(ffmpeg) ? new DeviceEnumerator(ffmpeg!) : null;
            }
        }
    }

    public bool MinimizeToTray
    {
        get => _settings.MinimizeToTray;
        set { if (_settings.MinimizeToTray != value) { _settings.MinimizeToTray = value; OnPropertyChanged(); } }
    }

    public string Theme
    {
        get => _settings.Theme;
        set { if (_settings.Theme != value) { _settings.Theme = value; OnPropertyChanged(); } }
    }

    // ===================================================================
    // Behaviour
    // ===================================================================

    /// <summary>Re-queries DirectShow video/audio devices via FFmpeg.</summary>
    public async Task RefreshDevicesAsync()
    {
        if (_deviceEnumerator is null)
        {
            return;
        }

        try
        {
            var (video, audio) = await _deviceEnumerator.ListDshowDevicesAsync();

            VideoDevices.Clear();
            foreach (var d in video)
            {
                VideoDevices.Add(d);
            }

            AudioDevices.Clear();
            foreach (var d in audio)
            {
                AudioDevices.Add(d);
            }

            SelectDefaultDevices();
        }
        catch
        {
            // Device enumeration is best-effort; ignore failures (e.g. no devices, ffmpeg error).
        }
    }

    /// <summary>
    /// Picks sensible default audio devices when none are chosen yet, so the dropdowns
    /// show real devices out of the box: the first microphone-like device for the mic, and
    /// the first loopback/"stereo mix"-style device for system audio (if one exists).
    /// </summary>
    private void SelectDefaultDevices()
    {
        if (string.IsNullOrWhiteSpace(MicDevice))
        {
            var mic = AudioDevices.FirstOrDefault(d => Contains(d, "micro"))
                      ?? AudioDevices.FirstOrDefault(d => Contains(d, "mic"))
                      ?? AudioDevices.FirstOrDefault();
            if (mic != null) MicDevice = mic;
        }

        if (string.IsNullOrWhiteSpace(SystemAudioDevice))
        {
            // Prefer a genuine loopback / "stereo mix"-style device for system audio…
            var sys = AudioDevices.FirstOrDefault(d =>
                Contains(d, "stereo mix") || Contains(d, "what u hear") || Contains(d, "loopback") ||
                Contains(d, "virtual-audio") || Contains(d, "cable output") || Contains(d, "wave out") ||
                Contains(d, "speakers"));
            // …otherwise fall back to the first device that isn't the chosen mic so the
            // dropdown is never blank (the user can change it; true system-audio capture
            // needs "Stereo Mix" enabled or a virtual audio cable installed).
            sys ??= AudioDevices.FirstOrDefault(d => !string.Equals(d, MicDevice, StringComparison.OrdinalIgnoreCase))
                    ?? AudioDevices.FirstOrDefault();
            if (sys != null) SystemAudioDevice = sys;
        }

        static bool Contains(string s, string term) =>
            s.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>Persists the (already mutated) settings to disk.</summary>
    public void Save() => SettingsService.Save(_settings);

    /// <summary>The underlying settings model, for callers that need it after editing.</summary>
    public AppSettings Settings => _settings;

    // ----- INotifyPropertyChanged --------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
