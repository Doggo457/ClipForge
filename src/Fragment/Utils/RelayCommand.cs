using System.Windows.Input;

namespace Fragment.Utils;

/// <summary>
/// A lightweight <see cref="ICommand"/> implementation for MVVM bindings.
/// Supports both parameterized and parameterless delegates and integrates
/// with WPF's <see cref="CommandManager"/> for automatic requery.
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    /// <summary>
    /// Creates a command from delegates that accept the command parameter.
    /// </summary>
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <summary>
    /// Creates a command from parameterless delegates. Convenient for buttons
    /// that do not pass a CommandParameter.
    /// </summary>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = _ => execute();
        _canExecute = canExecute is null ? null : _ => canExecute();
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    /// <summary>
    /// Forces WPF to re-query <see cref="CanExecute"/> for all commands.
    /// </summary>
    public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}
