using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VmQontrol.App.ViewModels;

/// <summary>One of the ways a prompt can be answered (a full or a linked clone), with what choosing it means.</summary>
public sealed partial class PromptOption(string title, string detail, bool chosen = false) : ObservableObject
{
    public string Title { get; } = title;

    public string Detail { get; } = detail;

    [ObservableProperty]
    private bool _chosen = chosen;
}

/// <summary>A question that needs a typed answer: a new name, or the machine's name to confirm a deletion; optionally a choice too.</summary>
public sealed partial class PromptViewModel(string title, IReadOnlyList<string> lines, string field, string text, string confirm, Func<string, bool> valid, bool isDanger = false,
    IReadOnlyList<PromptOption>? options = null) : ObservableObject
{
    /// <summary>The choices shown above the text field; empty for a plain question.</summary>
    public IReadOnlyList<PromptOption> Options { get; } = options ?? [];

    public bool HasOptions => Options.Count > 0;

    /// <summary>The position of the chosen option (0 when there are none).</summary>
    public int Choice => Math.Max(0, Options.ToList().FindIndex(o => o.Chosen));

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
