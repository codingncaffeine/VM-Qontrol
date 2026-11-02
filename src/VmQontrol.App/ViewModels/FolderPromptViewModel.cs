using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace VmQontrol.App.ViewModels;

/// <summary>"Move to folder": type a new folder's name or pick one that exists.</summary>
public sealed partial class FolderPromptViewModel(MachineViewModel machine, IReadOnlyList<string> existing) : ObservableObject
{
    public string Title => $"Move {machine.Name} to a folder";

    public IReadOnlyList<string> Existing { get; } = existing;

    public bool HasExisting => Existing.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveCommand))]
    private string _name = machine.FolderName;

    public event Action<string?>? Done;

    private bool CanMove() => Name.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanMove))]
    private void Move() => Done?.Invoke(Name);

    [RelayCommand]
    private void Pick(string folder) => Done?.Invoke(folder);

    [RelayCommand]
    private void Cancel() => Done?.Invoke(null);
}
