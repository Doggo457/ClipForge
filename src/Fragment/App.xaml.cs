using System.Windows;
using System.Windows.Threading;

namespace Fragment;

/// <summary>
/// Application entry point. Wires up global exception handling so that an
/// unhandled error surfaces to the user rather than silently terminating
/// the process.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Surface unhandled UI-thread exceptions instead of crashing silently.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"An unexpected error occurred:\n\n{e.Exception.Message}",
            "Fragment",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // Keep the app alive; the user can decide whether to continue.
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            MessageBox.Show(
                $"A fatal error occurred:\n\n{ex.Message}",
                "Fragment",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
