using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace SalesSupport.Client;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class QuestionVm : ViewModelBase
{
    public required string Id { get; init; }
    public required string Text { get; init; }
    public string? Thread { get; init; }

    private bool _fresh;
    public bool Fresh { get => _fresh; set => Set(ref _fresh, value); }

    private bool _asked;
    public bool Asked { get => _asked; set => Set(ref _asked, value); }
}

public sealed class ProductVm : ViewModelBase
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Why { get; init; }
    public string? Price { get; init; }
    public string? Thread { get; init; }

    private bool _fresh;
    public bool Fresh { get => _fresh; set => Set(ref _fresh, value); }
}

public sealed record ThreadVm(string Label, string Kind, string Status);

/// <summary>One row in the transcript log window: a merged utterance or a typed ask.</summary>
public sealed record TranscriptRowVm(string Time, string Label, string Text, bool IsRep, bool IsAsk);
