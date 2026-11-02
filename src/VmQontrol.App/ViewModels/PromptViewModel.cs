using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VmQontrol.App.ViewModels;

/// <summary>A question that needs a typed answer: a new name, or the machine's name to confirm a deletion.</summary>
public sealed partial class PromptViewModel(string title, IReadOnlyList<string> lines, string field, string text, string confirm, Func<string, bool> valid, bool isDanger = false) : ObservableObject
{
    private readonly TaskCompletionSource<string?> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Title { get; } = title;

    public IReadOnlyList<string> Lines { get; } = lines;

    public string Field { get; } = field;

    public string ConfirmText { get; } = confirm;

    public bool IsDanger { get; } = isDanger;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _text = text;

    [ObservableProperty]
    private bool _busy;

    [ObservableProperty]
    private string _progress = "";

    public Task<string?> Answer => _answer.Task;

    public event Action? Closed;

    private bool CanConfirm() => !Busy && valid(Text);

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm() => _answer.TrySetResult(Text.Trim());

    [RelayCommand]
    private void Cancel()
    {
        _answer.TrySetResult(null);
        Closed?.Invoke();
    }

    public void Finish() => Closed?.Invoke();
}
