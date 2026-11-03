using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VmQontrol.App.ViewModels;

/// <summary>
/// A library folder's name: a new folder, a folder renamed, or a machine moved into one (type a name or pick a folder
/// that exists). The sidebar's folders are the library's groups (AppSettings.Groups); they hold no files.
/// </summary>
public sealed partial class GroupPromptViewModel(string title, string help, string confirm, string name, IReadOnlyList<string> existing) : ObservableObject
{
    /// <summary>The longest folder name kept (a sidebar heading shows about this much).</summary>
    public const int MaxLength = 60;

    public string Title { get; } = title;

    public string Help { get; } = help;

    public string ConfirmText { get; } = confirm;

    public IReadOnlyList<string> Existing { get; } = existing;

    public bool HasExisting => Existing.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _name = name;

    public event Action<string?>? Done;

    private bool CanConfirm() => Name.Trim().Length is > 0 and <= MaxLength;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm() => Done?.Invoke(Name.Trim());

    [RelayCommand]
    private void Pick(string folder) => Done?.Invoke(folder);

    [RelayCommand]
    private void Cancel() => Done?.Invoke(null);
}
