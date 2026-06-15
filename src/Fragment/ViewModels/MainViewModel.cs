using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Fragment.Models;
using Fragment.Services;
using Fragment.Utils;

namespace Fragment.ViewModels
{
    /// <summary>
    /// Primary view model for <c>MainWindow</c>. Loads settings, locates FFmpeg, wires the
    /// recorder / replay-buffer / hotkey services and surfaces commands + observable state
    /// to the dark-themed main UI.
    /// </summary>
    public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
    {
        // Hotkey action identifiers. The HotkeyService returns the registration id it
        // assigned; we map those ids back to logical actions here.
        private int _recordHotkeyId = -1;
        private int _clipHotkeyId = -1;
        private int _replayHotkeyId = -1;

        private readonly AppSettings _settings;
        private string? _ffmpegPath;
        private ScreenRecorder? _recorder;
        private ReplayBufferService? _replayBuffer;
        private readonly HotkeyService _hotkeys;
        private readonly DispatcherTimer _timer;

        private bool _isInitializing = true;
        private bool _disposed;
        private DateTime _recordStartedUtc;

        private string _statusText = "Idle";
        private bool _isRecording;
        private bool _isReplayRunning;
        private string _recordTimer = "00:00:00";
        private string _selectedProfileName = "Default";

        public MainViewModel()
        {
            _settings = SettingsService.Load();

            ProfileNames = new ObservableCollection<string>(
                _settings.Profiles.Select(p => p.Name));

            _selectedProfileName =
                ProfileNames.Contains(_settings.ActiveProfileName)
                    ? _settings.ActiveProfileName
                    : (ProfileNames.FirstOrDefault() ?? "Default");

            _statusText = "Starting up…";

            _hotkeys = new HotkeyService();
            _hotkeys.HotkeyPressed += OnHotkeyPressed;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += (_, _) => UpdateTimer();

            StartStopCommand = new RelayCommand(_ => ToggleRecording(), _ => _recorder != null && !_isInitializing);
            SaveClipCommand = new RelayCommand(async _ => await SaveClipAsync(),
                _ => _replayBuffer is { IsRunning: true });
            ToggleReplayCommand = new RelayCommand(_ => ToggleReplay(), _ => _replayBuffer != null);
            OpenSettingsCommand = new RelayCommand(_ => OpenSettings());
            OpenTrimmerCommand = new RelayCommand(_ => OpenTrimmer());
            OpenOutputFolderCommand = new RelayCommand(_ => OpenOutputFolder());
        }

        /// <summary>
        /// Ensures FFmpeg is available (downloading it on first run if needed), wires up the
        /// recorder + replay-buffer services and auto-starts the replay buffer. Called once the
        /// window has loaded so Fragment is "launch and record" out of the box.
        /// </summary>
        public async System.Threading.Tasks.Task InitializeAsync()
        {
            var progress = new Progress<string>(s => StatusText = s);

            // Reclaim replay-buffer temp dirs left behind by previous (possibly crashed) sessions.
            ReplayBufferService.CleanupStaleBuffers();

            try
            {
                _ffmpegPath = await FfmpegProvider.EnsureAsync(_settings.FfmpegPath, progress);
            }
            catch (Exception ex)
            {
                StatusText = $"FFmpeg setup failed: {ex.Message}";
            }

            if (FfmpegLocator.IsValid(_ffmpegPath))
            {
                if (!string.Equals(_settings.FfmpegPath, _ffmpegPath, StringComparison.OrdinalIgnoreCase))
                {
                    _settings.FfmpegPath = _ffmpegPath;
                    SettingsService.Save(_settings);
                }

                // Adopt GPU hardware encoding when available so recording barely touches the CPU.
                // Only replace profiles still on the software default (never override a user's choice).
                try
                {
                    StatusText = "Detecting GPU encoder…";
                    var best = await HardwareEncoderDetector.DetectBestAsync(_ffmpegPath!);
                    if (best.HasValue)
                    {
                        bool changed = false;
                        foreach (var pr in _settings.Profiles)
                        {
                            if (pr.Encoder == VideoEncoder.x264)
                            {
                                pr.Encoder = best.Value;
                                changed = true;
                            }
                        }
                        if (changed)
                        {
                            SettingsService.Save(_settings);
                        }
                    }
                }
                catch
                {
                    // Detection is best-effort; fall back to the software encoder.
                }

                // Pick a default microphone so "System + Mic" captures both straight away.
                try
                {
                    bool needsMic = _settings.Profiles.Any(p =>
                        (p.Audio is AudioMode.MicOnly or AudioMode.SystemAndMic) && string.IsNullOrWhiteSpace(p.MicDevice));
                    if (needsMic)
                    {
                        var (_, audioDevices) = await new DeviceEnumerator(_ffmpegPath!).ListDshowDevicesAsync();
                        var mic = audioDevices.FirstOrDefault(d => d.Contains("micro", StringComparison.OrdinalIgnoreCase))
                                  ?? audioDevices.FirstOrDefault();
                        if (mic is not null)
                        {
                            bool changed = false;
                            foreach (var p in _settings.Profiles)
                            {
                                if ((p.Audio is AudioMode.MicOnly or AudioMode.SystemAndMic) && string.IsNullOrWhiteSpace(p.MicDevice))
                                {
                                    p.MicDevice = mic;
                                    changed = true;
                                }
                            }
                            if (changed) SettingsService.Save(_settings);
                        }
                    }
                }
                catch
                {
                    // Best-effort; recording still works with system audio only.
                }

                // The window may have been closed while we were downloading/detecting. Bail so we
                // don't spawn a recorder/replay-buffer that nothing will ever stop (orphaned ffmpeg).
                if (_disposed)
                {
                    return;
                }

                _recorder = new ScreenRecorder(_ffmpegPath!);
                _recorder.Started += OnRecorderStarted;
                _recorder.Stopped += OnRecorderStopped;
                _recorder.Error += OnRecorderError;

                _replayBuffer = new ReplayBufferService(_ffmpegPath!);
                _replayBuffer.Stopped += OnReplayBufferStopped;

                _isInitializing = false;
                OnPropertyChanged(nameof(RecordButtonText));
                StatusText = "Ready — press Record";

                if (_settings.ReplayBufferEnabled)
                {
                    StartReplayBuffer();
                }
            }
            else
            {
                _isInitializing = false;
                OnPropertyChanged(nameof(RecordButtonText));
                StatusText = "FFmpeg unavailable — set its path in Settings";
            }

            RefreshCanExecute();
        }

        // ---------------------------------------------------------------------
        // Window / hotkey wiring (called from the code-behind once the HWND exists)
        // ---------------------------------------------------------------------

        /// <summary>Exposed so the window's WndProc hook can forward messages.</summary>
        public HotkeyService Hotkeys => _hotkeys;

        /// <summary>
        /// Invoked by the window once its native handle is available. Initializes the
        /// hotkey service and registers the configured global hotkeys.
        /// </summary>
        public void AttachWindow(IntPtr windowHandle)
        {
            _hotkeys.Initialize(windowHandle);
            RegisterHotkeys();
        }

        private void RegisterHotkeys()
        {
            try
            {
                _hotkeys.UnregisterAll();

                var record = HotkeyService.ParseHotkey(_settings.RecordHotkey);
                _recordHotkeyId = _hotkeys.Register(record.mods, record.vk);

                var clip = HotkeyService.ParseHotkey(_settings.ClipHotkey);
                _clipHotkeyId = _hotkeys.Register(clip.mods, clip.vk);

                var replay = HotkeyService.ParseHotkey(_settings.ReplayToggleHotkey);
                _replayHotkeyId = _hotkeys.Register(replay.mods, replay.vk);
            }
            catch (Exception ex)
            {
                StatusText = $"Hotkey registration failed: {ex.Message}";
            }
        }

        private void OnHotkeyPressed(int id)
        {
            // Marshal onto the UI thread just in case the service raises on another thread.
            void Dispatch(Action a) =>
                Application.Current?.Dispatcher.Invoke(a);

            if (id == _recordHotkeyId)
            {
                Dispatch(ToggleRecording);
            }
            else if (id == _clipHotkeyId)
            {
                Dispatch(async () => await SaveClipAsync());
            }
            else if (id == _replayHotkeyId)
            {
                Dispatch(ToggleReplay);
            }
        }

        // ---------------------------------------------------------------------
        // Bindable properties
        // ---------------------------------------------------------------------

        public ObservableCollection<string> ProfileNames { get; }

        public string SelectedProfileName
        {
            get => _selectedProfileName;
            set
            {
                if (SetField(ref _selectedProfileName, value) && !string.IsNullOrEmpty(value))
                {
                    _settings.ActiveProfileName = value;
                    SettingsService.Save(_settings);
                }
            }
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetField(ref _statusText, value);
        }

        public bool IsRecording
        {
            get => _isRecording;
            private set
            {
                if (SetField(ref _isRecording, value))
                {
                    OnPropertyChanged(nameof(RecordButtonText));
                }
            }
        }

        public bool IsReplayRunning
        {
            get => _isReplayRunning;
            private set
            {
                if (SetField(ref _isReplayRunning, value))
                {
                    OnPropertyChanged(nameof(ReplayStateText));
                }
            }
        }

        public string RecordTimer
        {
            get => _recordTimer;
            private set => SetField(ref _recordTimer, value);
        }

        /// <summary>Label for the large record button.</summary>
        public string RecordButtonText => _isInitializing ? "Setting up…" : (IsRecording ? "Stop" : "Record");

        /// <summary>Secondary label on the replay toggle.</summary>
        public string ReplayStateText => IsReplayRunning ? "(On)" : "(Off)";

        // ---------------------------------------------------------------------
        // Commands
        // ---------------------------------------------------------------------

        public ICommand StartStopCommand { get; }
        public ICommand SaveClipCommand { get; }
        public ICommand ToggleReplayCommand { get; }
        public ICommand OpenSettingsCommand { get; }
        public ICommand OpenTrimmerCommand { get; }
        public ICommand OpenOutputFolderCommand { get; }

        // ---------------------------------------------------------------------
        // Command implementations
        // ---------------------------------------------------------------------

        private async void ToggleRecording()
        {
            if (_recorder == null)
            {
                StatusText = "FFmpeg not found — set its path in Settings";
                return;
            }

            try
            {
                if (_recorder.IsRecording)
                {
                    StatusText = "Stopping...";
                    await _recorder.StopAsync();
                }
                else
                {
                    StatusText = "Starting...";
                    await _recorder.StartAsync(ActiveProfile());
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Error: {ex.Message}";
            }

            RefreshCanExecute();
        }

        private async System.Threading.Tasks.Task SaveClipAsync()
        {
            if (_replayBuffer is not { IsRunning: true })
            {
                StatusText = "Replay buffer is not running";
                return;
            }

            try
            {
                StatusText = "Saving clip...";
                var profile = ActiveProfile();
                string outputPath = BuildOutputPath(profile, "Clip");

                string? saved = await _replayBuffer.SaveClipAsync(
                    _settings.ClipLengthSeconds, outputPath);

                StatusText = saved != null
                    ? $"Saved clip: {Path.GetFileName(saved)}"
                    : "Failed to save clip";
            }
            catch (Exception ex)
            {
                StatusText = $"Clip error: {ex.Message}";
            }
        }

        private void ToggleReplay()
        {
            if (_replayBuffer == null)
            {
                StatusText = "FFmpeg not found — set its path in Settings";
                return;
            }

            try
            {
                if (_replayBuffer.IsRunning)
                {
                    _replayBuffer.Stop();
                    IsReplayRunning = false;
                    StatusText = "Replay buffer stopped";
                }
                else
                {
                    StartReplayBuffer();
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Replay error: {ex.Message}";
            }

            RefreshCanExecute();
        }

        private void StartReplayBuffer()
        {
            if (_replayBuffer == null)
            {
                return;
            }

            try
            {
                _replayBuffer.Start(ActiveProfile(), _settings.ReplayBufferSeconds);
                IsReplayRunning = _replayBuffer.IsRunning;
                StatusText = IsReplayRunning ? "Replay buffer running" : StatusText;
            }
            catch (Exception ex)
            {
                IsReplayRunning = false;
                StatusText = $"Replay error: {ex.Message}";
            }

            RefreshCanExecute();
        }

        private void OpenSettings()
        {
            try
            {
                var vm = new SettingsViewModel(_settings);
                var window = new Views.SettingsWindow(vm);

                if (Application.Current?.MainWindow is { } owner && !ReferenceEquals(owner, window))
                {
                    window.Owner = owner;
                }

                // Only apply changes if the user clicked Save (DialogResult == true). On Cancel the
                // edited copy is discarded, so the live settings/hotkeys are left untouched.
                if (window.ShowDialog() == true)
                {
                    ReloadProfiles();
                    RegisterHotkeys();
                }
            }
            catch (Exception ex)
            {
                StatusText = $"Settings error: {ex.Message}";
            }
        }

        private void OpenTrimmer()
        {
            try
            {
                if (!FfmpegLocator.IsValid(_ffmpegPath))
                {
                    StatusText = "FFmpeg not found — set its path in Settings";
                    return;
                }

                var window = new Views.TrimmerWindow(new ClipTrimmer(_ffmpegPath!));

                if (Application.Current?.MainWindow is { } owner && !ReferenceEquals(owner, window))
                {
                    window.Owner = owner;
                }

                window.ShowDialog();
            }
            catch (Exception ex)
            {
                StatusText = $"Trimmer error: {ex.Message}";
            }
        }

        private void OpenOutputFolder()
        {
            try
            {
                string folder = ActiveProfile().OutputFolder;
                Directory.CreateDirectory(folder);

                Process.Start(new ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                StatusText = $"Could not open folder: {ex.Message}";
            }
        }

        // ---------------------------------------------------------------------
        // Recorder event handlers
        // ---------------------------------------------------------------------

        private void OnRecorderStarted(object? sender, EventArgs e)
        {
            void Apply()
            {
                IsRecording = true;
                _recordStartedUtc = DateTime.UtcNow;
                RecordTimer = "00:00:00";
                _timer.Start();
                StatusText = "Recording";
                RefreshCanExecute();
            }

            Application.Current?.Dispatcher.Invoke(Apply);
        }

        private void OnRecorderStopped(object? sender, string outputPath)
        {
            void Apply()
            {
                IsRecording = false;
                _timer.Stop();
                UpdateTimer();
                StatusText = string.IsNullOrEmpty(outputPath)
                    ? "Stopped"
                    : $"Saved: {Path.GetFileName(outputPath)}";
                RefreshCanExecute();
            }

            Application.Current?.Dispatcher.Invoke(Apply);
        }

        private void OnReplayBufferStopped(object? sender, EventArgs e)
        {
            // Fired when the buffer ffmpeg exits on its own (crash / encoder failure), not via Stop().
            Application.Current?.Dispatcher.Invoke(() =>
            {
                if (_disposed) return;
                IsReplayRunning = false;
                if (!IsRecording)
                {
                    StatusText = "Replay buffer stopped unexpectedly";
                }
                RefreshCanExecute();
            });
        }

        private void OnRecorderError(object? sender, string message)
        {
            void Apply()
            {
                IsRecording = false;
                _timer.Stop();
                StatusText = $"Error: {message}";
                RefreshCanExecute();
            }

            Application.Current?.Dispatcher.Invoke(Apply);
        }

        // ---------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------

        private RecordingProfile ActiveProfile()
        {
            return _settings.Profiles.FirstOrDefault(
                       p => string.Equals(p.Name, SelectedProfileName, StringComparison.Ordinal))
                   ?? _settings.ActiveProfile();
        }

        private void ReloadProfiles()
        {
            string previous = SelectedProfileName;

            ProfileNames.Clear();
            foreach (var p in _settings.Profiles)
            {
                ProfileNames.Add(p.Name);
            }

            _selectedProfileName = ProfileNames.Contains(previous)
                ? previous
                : (ProfileNames.FirstOrDefault() ?? "Default");
            OnPropertyChanged(nameof(SelectedProfileName));
        }

        private static string BuildOutputPath(RecordingProfile profile, string suffix)
        {
            Directory.CreateDirectory(profile.OutputFolder);

            DateTime now = DateTime.Now;
            string name = profile.FileNameTemplate
                .Replace("{date}", now.ToString("yyyy-MM-dd"))
                .Replace("{time}", now.ToString("HH-mm-ss"));

            if (!string.IsNullOrEmpty(suffix))
            {
                name += "_" + suffix;
            }

            string ext = ScreenRecorder.ContainerExtension(profile.Container);

            // Ensure a unique filename so two clips saved in the same second don't overwrite each other.
            string candidate = Path.Combine(profile.OutputFolder, name + ext);
            int index = 2;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(profile.OutputFolder, $"{name}_{index}{ext}");
                index++;
            }
            return candidate;
        }

        private void UpdateTimer()
        {
            if (!IsRecording)
            {
                return;
            }

            TimeSpan elapsed = DateTime.UtcNow - _recordStartedUtc;
            RecordTimer = elapsed.ToString(@"hh\:mm\:ss");
        }

        private void RefreshCanExecute()
        {
            (StartStopCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (SaveClipCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ToggleReplayCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }

        // ---------------------------------------------------------------------
        // INotifyPropertyChanged
        // ---------------------------------------------------------------------

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        public void Dispose()
        {
            _disposed = true; // tells an in-flight InitializeAsync not to spawn services after close
            _timer.Stop();

            if (_recorder != null)
            {
                _recorder.Started -= OnRecorderStarted;
                _recorder.Stopped -= OnRecorderStopped;
                _recorder.Error -= OnRecorderError;

                if (_recorder.IsRecording)
                {
                    // Finalize an in-progress recording so the file isn't corrupt.
                    try { _recorder.StopAsync().GetAwaiter().GetResult(); }
                    catch { /* best effort on shutdown */ }
                }
            }

            if (_replayBuffer != null)
            {
                _replayBuffer.Stopped -= OnReplayBufferStopped;
                try { _replayBuffer.Stop(); }
                catch { /* best effort */ }
            }

            _hotkeys.HotkeyPressed -= OnHotkeyPressed;
            _hotkeys.Dispose();
        }
    }
}
