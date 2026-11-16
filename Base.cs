using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace BlackStarSwissAudio.ViewModels;

public abstract class Obs : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n)); return true;
    }
    protected void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class RelayCommand(Action<object?> exec, Func<object?, bool>? can = null) : ICommand
{
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? parameter) => can?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => exec(parameter);
}

/// <summary>Async command: re-entrancy guarded, exceptions are routed to the supplied handler (never lost).</summary>
public sealed class AsyncCommand(Func<object?, Task> exec, Action<Exception> onError, Func<object?, bool>? can = null) : ICommand
{
    bool _running;
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? parameter) => !_running && (can?.Invoke(parameter) ?? true);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true; CommandManager.InvalidateRequerySuggested();
        try { await exec(parameter); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { onError(ex); }
        finally { _running = false; CommandManager.InvalidateRequerySuggested(); }
    }
}
