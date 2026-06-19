using System;
using System.Windows;
using System.Windows.Interop;
using Fragment.ViewModels;

namespace Fragment
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml.
    /// Owns the <see cref="HwndSource"/> wiring required for global hotkey registration,
    /// forwarding raw window messages to the <see cref="Fragment.Services.HotkeyService"/>
    /// exposed by the <see cref="MainViewModel"/>.
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private HwndSource? _hwndSource;
        private System.Windows.Forms.NotifyIcon? _tray;
        private bool _exiting; // true once the user really wants to quit (tray Exit / shutdown)

        public MainWindow()
        {
            InitializeComponent();

            _viewModel = new MainViewModel();
            DataContext = _viewModel;

            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private void SetupTray()
        {
            try
            {
                _tray = new System.Windows.Forms.NotifyIcon { Text = "Fragment", Visible = false };
                try { if (Environment.ProcessPath is { } p) _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(p); } catch { }
                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("Open Fragment", null, (_, _) => RestoreFromTray());
                menu.Items.Add("Exit", null, (_, _) => { _exiting = true; Close(); });
                _tray.ContextMenuStrip = menu;
                _tray.DoubleClick += (_, _) => RestoreFromTray();
            }
            catch { _tray = null; }
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            if (_tray != null) _tray.Visible = false;
        }

        // Minimize-to-tray: intercept close and hide to the tray instead of exiting (keeps the replay
        // buffer running). The tray menu's Exit (or app shutdown) sets _exiting so the window really closes.
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_exiting && _tray != null && _viewModel.MinimizeToTray)
            {
                e.Cancel = true;
                Hide();
                _tray.Visible = true;
                try { _tray.ShowBalloonTip(2000, "Fragment", "Still running — right-click the tray icon to Exit.", System.Windows.Forms.ToolTipIcon.Info); } catch { }
            }
            base.OnClosing(e);
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Obtain the native window handle and attach a message hook so the
            // HotkeyService can receive WM_HOTKEY messages.
            var helper = new WindowInteropHelper(this);
            IntPtr handle = helper.EnsureHandle();

            _hwndSource = HwndSource.FromHwnd(handle);
            _hwndSource?.AddHook(WndProc);

            SetupTray();

            // Hand the validated handle to the view model so it can register hotkeys.
            _viewModel.AttachWindow(handle);

            // Provision FFmpeg (downloading on first run) and enable recording.
            await _viewModel.InitializeAsync();
        }

        /// <summary>
        /// Forwards native window messages to the hotkey service. Returns the message
        /// unhandled so normal WPF processing continues.
        /// </summary>
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_viewModel.Hotkeys.ProcessMessage(msg, wParam))
            {
                handled = true;
            }

            return IntPtr.Zero;
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _hwndSource?.RemoveHook(WndProc);
            _hwndSource = null;
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
            _viewModel.Dispose();
        }
    }
}
