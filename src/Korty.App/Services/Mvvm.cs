using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Korty.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _exec;
    private readonly Func<object?, bool>? _can;

    public RelayCommand(Action exec, Func<bool>? can = null) : this(_ => exec(), can == null ? null : _ => can()) { }
    public RelayCommand(Action<object?> exec, Func<object?, bool>? can = null) { _exec = exec; _can = can; }

    public bool CanExecute(object? p) => _can?.Invoke(p) ?? true;
    public void Execute(object? p) => _exec(p);
    public event EventHandler? CanExecuteChanged;
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>Runs a task without re-entry, surfaces exceptions to the host (never to the dispatcher crash path).</summary>
public sealed class AsyncCommand : ICommand
{
    private readonly Func<object?, Task> _exec;
    private readonly Func<object?, bool>? _can;
    private readonly Action<Exception>? _onError;
    private bool _running;

    public AsyncCommand(Func<Task> exec, Func<bool>? can = null, Action<Exception>? onError = null)
        : this(_ => exec(), can == null ? null : _ => can(), onError) { }
    public AsyncCommand(Func<object?, Task> exec, Func<object?, bool>? can = null, Action<Exception>? onError = null)
    { _exec = exec; _can = can; _onError = onError; }

    public bool IsRunning => _running;
    public bool CanExecute(object? p) => !_running && (_can?.Invoke(p) ?? true);

    public async void Execute(object? p)
    {
        if (_running) return;
        _running = true; Refresh();
        try { await _exec(p); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _onError?.Invoke(ex); }
        finally { _running = false; Refresh(); }
    }

    public event EventHandler? CanExecuteChanged;
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _exec;
    private readonly Func<T?, bool>? _can;
    public RelayCommand(Action<T?> exec, Func<T?, bool>? can = null) { _exec = exec; _can = can; }
    public bool CanExecute(object? p) => _can?.Invoke(p is T t ? t : default) ?? true;
    public void Execute(object? p) => _exec(p is T t ? t : default);
    public event EventHandler? CanExecuteChanged;
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
