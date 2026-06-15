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

        public MainWindow()
        {
            InitializeComponent();

            _viewModel = new MainViewModel();
            DataContext = _viewModel;

            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Obtain the native window handle and attach a message hook so the
            // HotkeyService can receive WM_HOTKEY messages.
            var helper = new WindowInteropHelper(this);
            IntPtr handle = helper.EnsureHandle();

            _hwndSource = HwndSource.FromHwnd(handle);
            _hwndSource?.AddHook(WndProc);

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
            _viewModel.Dispose();
        }
    }
}
