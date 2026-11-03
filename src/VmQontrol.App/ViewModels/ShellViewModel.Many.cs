using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmQontrol.Core.Machines;

namespace VmQontrol.App.ViewModels;

/// <summary>
/// Several machines selected in the library (phase 19 item 1, D35): Ctrl+click adds or removes one, Shift+click a range,
/// Ctrl+A all. With two or more, the page shows them together with the actions that make sense for many: power on, shut
/// down, move to a folder, favorites, delete. Each machine still runs its own checks (memory, missing files, running).
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>The machines selected in the library, in the list's order; the page shows them together from two.</summary>
    public ObservableCollection<MachineViewModel> Chosen { get; } = [];

    public bool ShowMany => Chosen.Count > 1 && Wizard is null && SettingsPage is null && ImportPage is null && PreferencesPage is null;

    public string ManyTitle => $"{Chosen.Count} machines selected";

    public string ManySummary
    {
        get
        {
            var running = Chosen.Count(m => m.IsActive);
            var off = Chosen.Count - running;
            var parts = new List<string>();
            if (running > 0) parts.Add($"{running} running");
            if (off > 0) parts.Add($"{off} off");
            return string.Join(", ", parts) + ". Ctrl+click adds or removes one, Shift+click a range, Esc goes back to one.";
        }
    }

    private int ManyOff => Chosen.Count(m => m.CanPowerOn);

    private int ManyRunning => Chosen.Count(m => m.IsRunning);

    public string ManyPowerOnText => ManyOff == 0 ? "Power on" : ManyOff == Chosen.Count ? $"Power on all {Chosen.Count}" : $"Power on {ManyOff}";

    public string ManyShutDownText => ManyRunning == 0 ? "Shut down" : ManyRunning == Chosen.Count ? $"Shut down all {Chosen.Count}" : $"Shut down {ManyRunning}";

    public string ManyFavoriteText => Chosen.All(m => m.IsFavorite) ? "Remove from favorites" : "Add to favorites";

    public string ManyDeleteText => $"Delete {Chosen.Count}…";

    /// <summary>Called by the library list whenever its selection changes (the list's order is kept).</summary>
    public void SetChosen(IReadOnlyList<MachineViewModel> machines)
    {
        if (machines.SequenceEqual(Chosen)) return;
        foreach (var m in Chosen) m.PropertyChanged -= ChosenChanged;
        Chosen.Clear();
        foreach (var m in machines)
        {
            Chosen.Add(m);
            m.PropertyChanged += ChosenChanged;
        }

        RaiseMany();
        OnPropertyChanged(nameof(ShowWelcome));
        OnPropertyChanged(nameof(ShowMachine));
    }

    private void ChosenChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MachineViewModel.State) or nameof(MachineViewModel.IsFavorite)) RaiseMany();
    }

    private void RaiseMany()
    {
        foreach (var name in (string[])[nameof(ShowMany), nameof(ManyTitle), nameof(ManySummary), nameof(ManyPowerOnText), nameof(ManyShutDownText), nameof(ManyFavoriteText), nameof(ManyDeleteText)])
            OnPropertyChanged(name);
        PowerOnChosenCommand.NotifyCanExecuteChanged();
        ShutDownChosenCommand.NotifyCanExecuteChanged();
        DeleteChosenCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Raised to ask the library list to select only these (Esc on the many page, or after a delete).</summary>
    public event Action<IReadOnlyList<MachineViewModel>>? ChosenReset;

    [RelayCommand]
    public void BackToOne()
    {
        var keep = Selected is { } s && Chosen.Contains(s) ? s : Chosen.FirstOrDefault();
        ChosenReset?.Invoke(keep is null ? [] : [keep]);
    }

    private bool CanPowerOnChosen() => ManyOff > 0;

    /// <summary>Powers on every selected machine that is off, one after another, without opening their screens.</summary>
    [RelayCommand(CanExecute = nameof(CanPowerOnChosen))]
    private async Task PowerOnChosenAsync()
    {
        var machines = Chosen.Where(m => m.CanPowerOn).ToList();
        Note($"Powering on {machines.Count} machine{(machines.Count == 1 ? "" : "s")}…");
        foreach (var m in machines) await m.PowerOnInBackgroundCommand.ExecuteAsync(null);
    }

    private bool CanShutDownChosen() => ManyRunning > 0;

    /// <summary>Asks every selected running machine to shut down, as its power button would.</summary>
    [RelayCommand(CanExecute = nameof(CanShutDownChosen))]
    private async Task ShutDownChosenAsync()
    {
        var machines = Chosen.Where(m => m.IsRunning).ToList();
        Note($"Asking {machines.Count} machine{(machines.Count == 1 ? "" : "s")} to shut down.");
        foreach (var m in machines) await m.ShutDownCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void MoveChosen()
    {
        var machines = Chosen.ToList();
        AskName(new GroupPromptViewModel($"Move {machines.Count} machines to a folder", "Pick a folder, or type a new folder's name. Leave it empty to take them out of their folders.", "Move", "", GroupNames),
            name =>
            {
                foreach (var m in machines) SetGroup(m, name);
                Note(name.Trim().Length == 0 ? $"{machines.Count} machines are no longer in a folder." : $"{machines.Count} machines are in {name.Trim()}.");
            });
    }

    [RelayCommand]
    private void FavoriteChosen()
    {
        var on = !Chosen.All(m => m.IsFavorite);
        foreach (var m in Chosen.ToList()) SetFavorite(m, on);
        RaiseMany();
    }

    private bool CanDeleteChosen() => Chosen.Count > 1;

    /// <summary>
    /// Deletes the selected machines after one confirmation that lists each (typing how many). Running machines and those
    /// that cannot be deleted from here are named and left alone; the rest are deleted one by one.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDeleteChosen))]
    private async Task DeleteChosenAsync()
    {
        var machines = Chosen.ToList();
        var roots = LibraryRoots;
        var plans = await Task.Run(() => machines.Select(m => (Machine: m, Plan: m.IsActive ? ("It is running: power it off first.", (IReadOnlyList<string>)[]) : m.DeletionPlan(roots))).ToList()).ConfigureAwait(false);
        var go = plans.Where(p => p.Plan.Item1 is null).Select(p => p.Machine).ToList();
        var kept = plans.Where(p => p.Plan.Item1 is not null).Select(p => $"    {p.Machine.Name}: {p.Plan.Item1}").ToList();
        if (go.Count == 0)
        {
            Ui.Post(() => Show(new NoticeViewModel("None of these can be deleted now", kept, isError: true)));
            return;
        }

        var files = plans.Where(p => p.Plan.Item1 is null).Sum(p => p.Plan.Item2.Count);
        var prompt = new PromptViewModel($"Delete {go.Count} machine{(go.Count == 1 ? "" : "s")}?",
            ["These machines and everything in them are deleted for good:", .. go.Select(m => "    " + m.Name), $"({files} files in their folders.)",
             .. kept.Count > 0 ? ["Left alone:", .. kept] : Array.Empty<string>(),
             "Installer discs kept elsewhere, and shared disks other machines still use, are not touched.", $"Type {go.Count} to confirm."],
            "How many", "", "Delete for good", t => t.Trim() == go.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), isDanger: true);
        Ui.Post(() => ShowPrompt(prompt));
        if (await prompt.Answer.ConfigureAwait(false) is null) return;
        var failed = new List<string>();
        await Task.Run(() =>
        {
            foreach (var m in go)
            {
                try
                {
                    m.DeleteFiles(roots);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed.Add($"{m.Name}: {ex.Message}");
                }
            }

            MachineLifecycle.PruneBases(roots);
        }).ConfigureAwait(false);
        Ui.Post(() =>
        {
            prompt.Finish();
            ChosenReset?.Invoke([]);
            if (failed.Count > 0) Show(new NoticeViewModel("Some machines could not be deleted", failed, isError: true));
            else Note($"{go.Count} machines were deleted.");
        });
        await AfterLifecycleAsync(null).ConfigureAwait(false);
    }
}
