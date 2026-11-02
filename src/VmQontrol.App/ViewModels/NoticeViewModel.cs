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

    public NoticeViewModel(string title, IReadOnlyList<string> lines, string? confirm = null, bool isError = false, bool isDanger = false,
        string? dismiss = null, string? checkText = null, string? icon = null, string? itemTitle = null, string? itemDetail = null, string? code = null)
    {
        Code = code;
        Icon = icon is not null && Avalonia.Application.Current?.TryGetResource(icon, null, out var g) == true ? g as Avalonia.Media.Geometry : null;
        ItemTitle = itemTitle;
        ItemDetail = itemDetail;
        DismissOverride = dismiss;
        CheckText = checkText;
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

    private string? DismissOverride { get; }

    public string DismissText => DismissOverride ?? (ConfirmText is null ? "OK" : "Cancel");

    /// <summary>A check box under the lines ("Always connect it"), read with the answer; <c>null</c> for none.</summary>
    public string? CheckText { get; }

    public bool HasCheck => CheckText is not null;

    [ObservableProperty]
    private bool _checked;

    public bool IsError { get; }

    /// <summary>A picture for what the notice is about (a USB plug), drawn in an accent tile beside the title.</summary>
    public Avalonia.Media.Geometry? Icon { get; }

    public bool HasIcon => Icon is not null;

    /// <summary>The thing the question is about (a device), shown as its own inset row: a name and a quiet detail line.</summary>
    public string? ItemTitle { get; }

    public string? ItemDetail { get; }

    public bool HasItem => ItemTitle is not null;

    /// <summary>A command to show as code under the lines (selectable; the confirm button usually copies it).</summary>
    public string? Code { get; }

    public bool HasCode => Code is not null;

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
