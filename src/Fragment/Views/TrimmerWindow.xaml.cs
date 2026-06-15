using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Fragment.Services;
using Microsoft.Win32;

namespace Fragment.Views;

/// <summary>
/// Visual clip trimmer: previews the video, lets the user scrub a playhead and drag In/Out handles
/// on a timeline, then writes the selected range out via <see cref="ClipTrimmer"/>.
/// </summary>
public partial class TrimmerWindow : Window
{
    private readonly ClipTrimmer _trimmer;
    private readonly DispatcherTimer _tick;

    private string? _inputPath;
    private double _durationSec;
    private double _startSec;
    private double _endSec = 10;
    private double _positionSec;

    private bool _mediaReady;
    private bool _isPlaying;
    private bool _draggingPlayhead;
    private bool _busy;
    private bool _suppressBoxSync;

    public TrimmerWindow(ClipTrimmer trimmer, string? initialInputPath = null)
    {
        InitializeComponent();
        _trimmer = trimmer ?? throw new ArgumentNullException(nameof(trimmer));

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _tick.Tick += OnTick;
        _tick.Start();

        Loaded += (_, _) => LayoutTimeline();

        if (!string.IsNullOrWhiteSpace(initialInputPath) && File.Exists(initialInputPath))
        {
            LoadMedia(initialInputPath!);
        }
    }

    // ----------------------------------------------------------------- load
    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a video to trim",
            Filter = "Video files|*.mp4;*.mkv;*.mov;*.webm;*.avi;*.flv|All files|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            LoadMedia(dialog.FileName);
        }
    }

    // ----------------------------------------------------------------- drag & drop
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasDroppableFile(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var file = Array.Find(files, File.Exists);
            if (file is not null)
            {
                LoadMedia(file);
            }
        }
    }

    private static bool HasDroppableFile(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
        return e.Data.GetData(DataFormats.FileDrop) is string[] files && Array.Exists(files, File.Exists);
    }

    private void LoadMedia(string path)
    {
        _inputPath = path;
        InputBox.Text = path;
        _mediaReady = false;
        _isPlaying = false;
        _positionSec = 0;
        PlayButton.IsEnabled = false;
        PlayButton.Content = "▶ Play";
        PreviewHint.Text = "Loading…";
        PreviewHint.Visibility = Visibility.Visible;
        StatusText.Text = "Loading video…";

        try
        {
            Media.Stop();
            Media.Source = new Uri(path);
            Media.Play(); // needed to begin opening with LoadedBehavior=Manual; paused in MediaOpened
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not open file: {ex.Message}";
        }
    }

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        Media.Pause();
        _isPlaying = false;
        _mediaReady = true;

        _durationSec = Media.NaturalDuration.HasTimeSpan
            ? Media.NaturalDuration.TimeSpan.TotalSeconds
            : 0;

        _startSec = 0;
        _endSec = _durationSec > 0 ? _durationSec : _endSec;
        _positionSec = 0;
        Media.Position = TimeSpan.Zero;

        PreviewHint.Visibility = Visibility.Collapsed;
        PlayButton.IsEnabled = true;
        PlayButton.Content = "▶ Play";
        StatusText.Text = "Loaded — drag the In/Out handles, or scrub and use Set In / Set Out.";

        UpdateUiFromState();
    }

    private void OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _mediaReady = false;
        PreviewHint.Text = "Preview unavailable for this file (codec not supported by Windows).";
        PreviewHint.Visibility = Visibility.Visible;
        PlayButton.IsEnabled = false;
        StatusText.Text = "Preview unavailable — you can still type Start/End below and Save the trim.";
        // Keep whatever duration the End box implies so the timeline + trim still function.
        if (_durationSec <= 0)
        {
            _durationSec = Math.Max(_endSec, 1);
        }
        UpdateUiFromState();
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        Media.Pause();
        _isPlaying = false;
        PlayButton.Content = "▶ Play";
    }

    // ----------------------------------------------------------------- transport
    private void OnPlayPauseClick(object sender, RoutedEventArgs e)
    {
        if (!_mediaReady) return;

        if (_isPlaying)
        {
            Media.Pause();
            _isPlaying = false;
            PlayButton.Content = "▶ Play";
        }
        else
        {
            // Start playback within the selected range.
            if (_positionSec < _startSec || _positionSec >= _endSec - 0.05)
            {
                Seek(_startSec);
            }
            Media.Play();
            _isPlaying = true;
            PlayButton.Content = "⏸ Pause";
        }
    }

    private void OnJumpStartClick(object sender, RoutedEventArgs e) => Seek(_startSec);
    private void OnJumpEndClick(object sender, RoutedEventArgs e) => Seek(Math.Max(_startSec, _endSec - 0.1));

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_isPlaying || !_mediaReady || _draggingPlayhead) return;

        _positionSec = Media.Position.TotalSeconds;
        if (_endSec > 0 && _positionSec >= _endSec)
        {
            Media.Pause();
            _isPlaying = false;
            _positionSec = _endSec;
            Media.Position = ToTs(_endSec);
            PlayButton.Content = "▶ Play";
        }
        UpdatePlayhead();
    }

    // ----------------------------------------------------------------- timeline scrub
    private void OnTimelineMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_durationSec <= 0) return;
        _draggingPlayhead = true;
        TimelineCanvas.CaptureMouse();
        SeekToX(e.GetPosition(TimelineCanvas).X);
    }

    private void OnTimelineMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingPlayhead && e.LeftButton == MouseButtonState.Pressed)
        {
            SeekToX(e.GetPosition(TimelineCanvas).X);
        }
    }

    private void OnTimelineMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggingPlayhead)
        {
            _draggingPlayhead = false;
            TimelineCanvas.ReleaseMouseCapture();
        }
    }

    private void SeekToX(double x)
    {
        double w = TimelineCanvas.ActualWidth;
        if (w <= 0 || _durationSec <= 0) return;
        double t = Clamp(x / w * _durationSec, 0, _durationSec);
        Seek(t);
    }

    private void Seek(double seconds)
    {
        _positionSec = Clamp(seconds, 0, _durationSec > 0 ? _durationSec : seconds);
        if (_mediaReady)
        {
            Media.Position = ToTs(_positionSec);
        }
        UpdatePlayhead();
    }

    // ----------------------------------------------------------------- In/Out handles
    private void OnInDrag(object sender, DragDeltaEventArgs e)
    {
        if (_durationSec <= 0) return;
        double dt = e.HorizontalChange / TimelineCanvas.ActualWidth * _durationSec;
        _startSec = Clamp(_startSec + dt, 0, _endSec - 0.1);
        if (_mediaReady) Seek(_startSec); // preview the new in-point frame
        UpdateUiFromState();
    }

    private void OnOutDrag(object sender, DragDeltaEventArgs e)
    {
        if (_durationSec <= 0) return;
        double dt = e.HorizontalChange / TimelineCanvas.ActualWidth * _durationSec;
        _endSec = Clamp(_endSec + dt, _startSec + 0.1, _durationSec);
        if (_mediaReady) Seek(_endSec);
        UpdateUiFromState();
    }

    private void OnSetInClick(object sender, RoutedEventArgs e)
    {
        _startSec = Clamp(_positionSec, 0, _endSec - 0.1);
        UpdateUiFromState();
    }

    private void OnSetOutClick(object sender, RoutedEventArgs e)
    {
        _endSec = Clamp(_positionSec, _startSec + 0.1, _durationSec > 0 ? _durationSec : _positionSec);
        UpdateUiFromState();
    }

    private void OnStartBoxCommitted(object sender, RoutedEventArgs e)
    {
        if (_suppressBoxSync) return;
        if (TryParseTime(StartBox.Text, out var t))
        {
            double max = _durationSec > 0 ? _durationSec : t.TotalSeconds;
            _startSec = Clamp(t.TotalSeconds, 0, Math.Max(0, Math.Min(max, _endSec - 0.1)));
        }
        UpdateUiFromState();
    }

    private void OnEndBoxCommitted(object sender, RoutedEventArgs e)
    {
        if (_suppressBoxSync) return;
        if (TryParseTime(EndBox.Text, out var t))
        {
            _endSec = Math.Max(_startSec + 0.1, t.TotalSeconds);
            if (_durationSec > 0) _endSec = Math.Min(_endSec, _durationSec);
            else _durationSec = _endSec; // no preview: let the End box define the timeline length
        }
        UpdateUiFromState();
    }

    // ----------------------------------------------------------------- trim
    private async void OnTrimClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (string.IsNullOrWhiteSpace(_inputPath) || !File.Exists(_inputPath))
        {
            StatusText.Text = "Choose an input file first.";
            return;
        }
        if (_endSec <= _startSec)
        {
            StatusText.Text = "The Out point must be after the In point.";
            return;
        }

        // Pause preview so ffmpeg isn't fighting the player for the file.
        Media.Pause();
        _isPlaying = false;
        PlayButton.Content = "▶ Play";

        var outputPath = BuildOutputPath(_inputPath!);
        try
        {
            SetBusy(true);
            StatusText.Text = "Trimming…";
            var result = await _trimmer.TrimAsync(
                _inputPath!, ToTs(_startSec), ToTs(_endSec), outputPath, ReEncodeCheck.IsChecked == true);
            StatusText.Text = $"Saved: {result}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Trim failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        TrimButton.IsEnabled = !busy;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _tick.Stop();
        try { Media.Stop(); Media.Close(); } catch { }
        base.OnClosed(e);
    }

    // ----------------------------------------------------------------- layout / formatting
    private void OnTimelineSizeChanged(object sender, SizeChangedEventArgs e) => LayoutTimeline();

    private void UpdateUiFromState()
    {
        _suppressBoxSync = true;
        StartBox.Text = Fmt(_startSec);
        EndBox.Text = Fmt(_endSec);
        _suppressBoxSync = false;
        DurationRun.Text = Fmt(_durationSec);
        UpdatePlayhead();
    }

    private void UpdatePlayhead()
    {
        PositionRun.Text = Fmt(_positionSec);
        LayoutTimeline();
    }

    private void LayoutTimeline()
    {
        double w = TimelineCanvas.ActualWidth;
        if (w <= 0) return;

        TrackRect.Width = w;

        if (_durationSec <= 0)
        {
            SelRect.Width = 0;
            return;
        }

        double sx = Clamp(_startSec / _durationSec * w, 0, w);
        double ex = Clamp(_endSec / _durationSec * w, 0, w);
        double px = Clamp(_positionSec / _durationSec * w, 0, w);

        System.Windows.Controls.Canvas.SetLeft(SelRect, sx);
        SelRect.Width = Math.Max(0, ex - sx);

        System.Windows.Controls.Canvas.SetLeft(PlayLine, px - PlayLine.Width / 2);
        System.Windows.Controls.Canvas.SetLeft(InThumb, sx - InThumb.Width / 2);
        System.Windows.Controls.Canvas.SetLeft(OutThumb, ex - OutThumb.Width / 2);
    }

    private static double Clamp(double v, double min, double max)
        => max < min ? min : (v < min ? min : (v > max ? max : v));

    private static TimeSpan ToTs(double seconds) => TimeSpan.FromSeconds(seconds < 0 ? 0 : seconds);

    private static string Fmt(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var t = TimeSpan.FromSeconds(seconds);
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}.{t.Milliseconds / 100}";
    }

    /// <summary>Parses "hh:mm:ss(.f)", "mm:ss(.f)", or plain seconds into a TimeSpan.</summary>
    private static bool TryParseTime(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();

        var inv = CultureInfo.InvariantCulture;
        if (!text.Contains(':') && double.TryParse(text, NumberStyles.Float, inv, out var secs))
        {
            value = TimeSpan.FromSeconds(secs);
            return value >= TimeSpan.Zero;
        }

        var parts = text.Split(':');
        try
        {
            switch (parts.Length)
            {
                case 2:
                    value = TimeSpan.FromMinutes(int.Parse(parts[0], inv))
                            + TimeSpan.FromSeconds(double.Parse(parts[1], inv));
                    return value >= TimeSpan.Zero;
                case 3:
                    value = TimeSpan.FromHours(int.Parse(parts[0], inv))
                            + TimeSpan.FromMinutes(int.Parse(parts[1], inv))
                            + TimeSpan.FromSeconds(double.Parse(parts[2], inv));
                    return value >= TimeSpan.Zero;
                default:
                    return false;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string BuildOutputPath(string inputPath)
    {
        var dir = Path.GetDirectoryName(inputPath) ?? Directory.GetCurrentDirectory();
        var name = Path.GetFileNameWithoutExtension(inputPath);
        var ext = Path.GetExtension(inputPath);
        if (string.IsNullOrEmpty(ext)) ext = ".mp4";

        var candidate = Path.Combine(dir, $"{name}_trim{ext}");
        var index = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(dir, $"{name}_trim_{index}{ext}");
            index++;
        }
        return candidate;
    }
}
