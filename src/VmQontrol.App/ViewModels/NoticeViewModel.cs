using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VmQontrol.App.ViewModels;

/// <summary>
/// A message over the window: an error with its reasons, or a question with a confirming button. Shown in the window
/// itself (no second window to lose behind the first).
/// </summary>
public sealed partial class NoticeViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public NoticeViewModel(string title, IReadOnlyList<string> lines, string? confirm = null, bool isError = false, bool isDanger = false)
    {
        Title = title;
        Lines = lines;
        ConfirmText = confirm;
        IsError = isError;
        IsDanger = isDanger;
    }

    public string Title { get; }

    public IReadOnlyList<string> Lines { get; }

    /// <summary>The confirming button's words ("Power off"); <c>null</c> for a message with only "OK".</summary>
    public string? ConfirmText { get; }

    public bool HasConfirm => ConfirmText is not null;

    public string DismissText => ConfirmText is null ? "OK" : "Cancel";

    public bool IsError { get; }

    public bool IsDanger { get; }

    public Task<bool> Answer => _answer.Task;

    public event Action? Closed;

    [RelayCommand]
    private void Confirm()
    {
        _answer.TrySetResult(true);
        Closed?.Invoke();
    }

    [RelayCommand]
    private void Dismiss()
    {
        _answer.TrySetResult(false);
        Closed?.Invoke();
    }
}
