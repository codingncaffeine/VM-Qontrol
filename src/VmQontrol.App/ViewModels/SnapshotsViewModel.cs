using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmQontrol.Core.Runtime;
using VmQontrol.Core.Snapshots;

namespace VmQontrol.App.ViewModels;

/// <summary>A snapshot in the tree: indented under its parent, with "You are here" on the one the machine stands on.</summary>
public sealed record SnapshotRow(Snapshot Snapshot, int Depth, bool IsCurrent, Bitmap? Picture)
{
    public string Name => Snapshot.Name;

    public string When => Snapshot.Taken.LocalDateTime.ToString("g", System.Globalization.CultureInfo.CurrentCulture);

    public string Kind => Snapshot.WithMemory ? "Taken while running: restores to that running moment" : "Taken while off: restores the disks";

    public string Description => Snapshot.Description;

    public bool HasDescription => Snapshot.Description.Length > 0;

    public Avalonia.Thickness Indent => new(Depth * 28, 0, 0, 0);
}

/// <summary>A machine's snapshots: take one at any moment, return to any of them, delete the ones no longer wanted.</summary>
public sealed partial class SnapshotsViewModel(MachineViewModel machine, ShellViewModel shell) : ObservableObject
{
    public ObservableCollection<SnapshotRow> Rows { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TakeCommand))]
    private string _newName = "";

    [ObservableProperty]
    private string _newDescription = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TakeCommand))]
    private bool _busy;

    [ObservableProperty]
    private string _note = "";

    public bool IsEmpty => Rows.Count == 0;

    internal SnapshotStore Store => new(machine.Machine, machine.Folder, machine.HistoryDirectory);

    public async Task LoadAsync()
    {
        var store = Store;
        var rows = await Task.Run(() =>
        {
            var list = store.Load();
            var result = new List<SnapshotRow>();
            void Walk(string? parent, int depth)
            {
                foreach (var s in list.Snapshots.Where(s => s.Parent == parent).OrderBy(s => s.Taken))
                {
                    Bitmap? picture = null;
                    if (s.Screenshot is { } file && File.Exists(Path.Combine(store.ScreenshotDirectory, file)))
                    {
                        using var stream = File.OpenRead(Path.Combine(store.ScreenshotDirectory, file));
                        picture = Bitmap.DecodeToWidth(stream, 240);
                    }

                    result.Add(new SnapshotRow(s, depth, s.Tag == list.Current, picture));
                    Walk(s.Tag, depth + 1);
                }
            }

            Walk(null, 0);
            return result;
        }).ConfigureAwait(false);
        Ui.Post(() =>
        {
            Rows.Clear();
            foreach (var r in rows) Rows.Add(r);
            OnPropertyChanged(nameof(IsEmpty));
            if (NewName.Length == 0) NewName = $"Snapshot {rows.Count + 1}";
        });
    }

    private bool CanTake() => !Busy && NewName.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanTake))]
    private async Task TakeAsync()
    {
        Busy = true;
        Note = machine.IsActive ? "Taking a snapshot of the running machine (memory included)…" : "Taking a snapshot…";
        var (name, description, store, watcher) = (NewName.Trim(), NewDescription.Trim(), Store, machine.Watcher);
        try
        {
            await Task.Run(() => store.TakeAsync(name, description, watcher)).ConfigureAwait(false);
            Ui.Post(() =>
            {
                Note = $"Snapshot \"{name}\" taken.";
                NewName = "";
                NewDescription = "";
            });
            await LoadAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is QmpException or IOException or InvalidOperationException)
        {
            Ui.Post(() => Note = $"The snapshot could not be taken: {ex.Message}");
        }
        finally
        {
            Ui.Post(() => Busy = false);
        }
    }

    [RelayCommand]
    private async Task RestoreAsync(SnapshotRow row)
    {
        var running = machine.IsActive;
        var notice = new NoticeViewModel($"Go back to \"{row.Name}\"?",
            [running && row.Snapshot.WithMemory
                ? "The machine returns to the moment the snapshot was taken, with the programs that were open then. Anything since is lost unless you take a snapshot first."
                : "The machine's disks return to how they were when the snapshot was taken. Anything since is lost unless you take a snapshot first."],
            confirm: "Go back", isDanger: true);
        shell.Show(notice);
        if (!await notice.Answer.ConfigureAwait(false)) return;
        var (store, watcher) = (Store, machine.Watcher);
        try
        {
            await Task.Run(() => store.RestoreAsync(row.Snapshot, watcher)).ConfigureAwait(false);
            Ui.Post(() => Note = $"Back at \"{row.Name}\".");
            await LoadAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is QmpException or IOException or InvalidOperationException)
        {
            Ui.Post(() => shell.Show(new NoticeViewModel("The snapshot could not be restored", [ex.Message], isError: true)));
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(SnapshotRow row)
    {
        var notice = new NoticeViewModel($"Delete \"{row.Name}\"?",
            ["The machine stays as it is now; only the way back to this moment is removed. Snapshots taken after it are kept."],
            confirm: "Delete snapshot", isDanger: true);
        shell.Show(notice);
        if (!await notice.Answer.ConfigureAwait(false)) return;
        var (store, watcher) = (Store, machine.Watcher);
        try
        {
            await Task.Run(() => store.DeleteAsync(row.Snapshot, watcher)).ConfigureAwait(false);
            Ui.Post(() => Note = $"\"{row.Name}\" deleted.");
            await LoadAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is QmpException or IOException or InvalidOperationException)
        {
            Ui.Post(() => shell.Show(new NoticeViewModel("The snapshot could not be deleted", [ex.Message], isError: true)));
        }
    }
}
