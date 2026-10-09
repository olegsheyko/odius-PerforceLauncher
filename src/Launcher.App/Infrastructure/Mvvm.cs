using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;

namespace PerforceLauncher.App.Infrastructure;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _exec;
    private readonly Func<object?, bool>? _can;

    public RelayCommand(Action<object?> exec, Func<object?, bool>? can = null) { _exec = exec; _can = can; }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _can?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _exec(parameter);
}

public sealed class AsyncRelayCommand : ICommand
{
    /// <summary>Единый обработчик ошибок для всех async-команд (показывает диалог).</summary>
    public static Action<Exception>? ErrorHandler { get; set; }

    private readonly Func<object?, Task> _exec;
    private readonly Func<object?, bool>? _can;
    private bool _running;

    public AsyncRelayCommand(Func<object?, Task> exec, Func<object?, bool>? can = null) { _exec = exec; _can = can; }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_running && (_can?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try { await _exec(parameter); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ErrorHandler?.Invoke(ex); }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
