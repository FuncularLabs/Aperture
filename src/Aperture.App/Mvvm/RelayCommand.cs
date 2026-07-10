using System.Windows.Input;

namespace Aperture.App.Mvvm;

/// <summary>A parameterless ICommand backed by delegates.</summary>
public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    private readonly Action _execute = execute;
    private readonly Func<bool>? _canExecute = canExecute;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();
}

/// <summary>An ICommand that receives a typed parameter.</summary>
public sealed class RelayCommand<T>(Action<T> execute, Func<T, bool>? canExecute = null) : ICommand
{
    private readonly Action<T> _execute = execute;
    private readonly Func<T, bool>? _canExecute = canExecute;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) =>
        _canExecute?.Invoke(parameter is T t ? t : default!) ?? true;

    public void Execute(object? parameter)
    {
        if (parameter is T t)
            _execute(t);
    }
}
