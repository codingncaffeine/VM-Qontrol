using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VmQontrol.App.ViewModels;

/// <summary>One command the palette offers: its words, where it applies, and what it does.</summary>
public sealed record PaletteItem(string Title, string Context, Action Run)
{
    /// <summary>The keyboard shortcut that does the same, shown beside it ("Ctrl+B"), or <c>null</c>.</summary>
    public string? Keys { get; init; }

    public bool HasKeys => Keys is not null;
}

/// <summary>
/// Ctrl+K: every action in one search box. Words match in any order, each as the start of a word or anywhere; earlier
/// and whole-word matches rank first. Enter runs the highlighted command.
/// </summary>
public sealed partial class PaletteViewModel(ShellViewModel shell) : ObservableObject
{
    /// <summary>The shortcut for an action, by the shortcut table's title (set by the window, which owns the table).</summary>
    public static Func<string, string?> KeysFor { get; set; } = _ => null;

    private static PaletteItem With(PaletteItem item, string shortcutTitle) => item with { Keys = KeysFor(shortcutTitle) };

    private List<PaletteItem> _all = [];

    public ObservableCollection<PaletteItem> Items { get; } = [];

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private PaletteItem? _highlighted;

    public void Open()
    {
        _all = Build();
        Query = "";
        Filter();
        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    public void RunHighlighted()
    {
        var item = Highlighted;
        Close();
        item?.Run();
    }

    public void Move(int delta)
    {
        if (Items.Count == 0) return;
        var index = Highlighted is null ? -1 : Items.IndexOf(Highlighted);
        Highlighted = Items[Math.Clamp(index + delta, 0, Items.Count - 1)];
    }

    partial void OnQueryChanged(string value) => Filter();

    private void Filter()
    {
        var words = Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var ranked = _all.Select(i => (Item: i, Score: Score(i, words))).Where(x => x.Score >= 0).OrderBy(x => x.Score).ThenBy(x => x.Item.Title, StringComparer.CurrentCultureIgnoreCase);
        Items.Clear();
        foreach (var (item, _) in ranked.Take(40)) Items.Add(item);
        Highlighted = Items.FirstOrDefault();
    }

    /// <summary>Lower is better; -1 = no match.</summary>
    public static int Score(PaletteItem item, IReadOnlyList<string> words)
    {
        var text = $"{item.Title} {item.Context}";
        var score = 0;
        foreach (var w in words)
        {
            var at = text.IndexOf(w, StringComparison.CurrentCultureIgnoreCase);
            if (at < 0) return -1;
            var wordStart = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            score += at + (wordStart ? 0 : 50);
        }

        return score;
    }

    private List<PaletteItem> Build()
    {
        var items = new List<PaletteItem>
        {
            With(new("New machine", "Create", shell.NewMachine), "New machine"),
            With(new("Preferences", "The app's own settings", shell.OpenPreferences), "Preferences"),
            new("About VM Qontrol", "Version, check for updates, guides", shell.OpenAbout),
            With(new("Import a machine", "From VMware, VirtualBox, an OVF appliance, virt-manager or a disk", shell.OpenImport), "Import a machine"),
            With(new("Refresh the library", "Library", () => _ = shell.RefreshAsync()), "Look for machines again"),
            new("Show the host check", "Welcome", () => shell.Selected = null),
            new("Virtual networks", "Networking", shell.OpenNetwork),
            new("Disks and discs", "Every disk and disc, its size, unused ones", shell.OpenMedia),
            With(new("New folder", "Library: sort machines into groups", shell.NewGroup), "New folder"),
            With(new("Keyboard shortcuts", "Help", () => shell.ShowShortcuts = true), "Keyboard shortcuts"),
        };
        foreach (var g in shell.GroupNames)
        {
            var folded = shell.Settings.CollapsedGroups.Contains(g);
            items.Add(new(folded ? $"Open folder {g}" : $"Fold folder {g}", "Library", () => shell.ToggleGroup(g)));
            items.Add(new($"Power on everything in {g}", "Library", () => shell.PowerOnGroupCommand.Execute(g)));
        }

        foreach (var m in shell.Machines) items.Add(new($"Go to {m.Name}", m.LibraryLabel, () => shell.Selected = m));
        if (shell.Selected is { } s)
        {
            void Add(string title, ICommand command, bool when = true, string? shortcut = null)
            {
                if (when) items.Add(With(new(title, s.Name, () => command.Execute(null)), shortcut ?? title));
            }

            Add(s.PowerOnText, s.PowerOnCommand, s.CanPowerOn, "Power on");
            Add("New machine from this template", s.CloneCommand, s.IsTemplate);
            Add("Power on in the background", s.PowerOnInBackgroundCommand, s.CanPowerOn);
            Add("Power on to firmware setup", s.PowerOnToFirmwareCommand, s.CanOpenFirmware);
            Add("Discard suspended state", s.DiscardSuspendedCommand, s.CanDiscardSuspended);
            Add("Open screen", s.OpenScreenCommand, s.IsActive, "Power on, or open the screen of a running machine");
            Add("Shut down", s.ShutDownCommand, s.IsRunning);
            Add("Pause", s.PauseCommand, s.CanPause, "Pause or resume");
            Add("Resume", s.ResumeCommand, s.IsPaused, "Pause or resume");
            Add("Suspend", s.SuspendCommand, s.IsActive, "Suspend (save its state and turn off)");
            Add("Reset", s.ResetCommand, s.IsActive, "Reset (asks first)");
            Add("Power off", s.PowerOffCommand, s.IsActive, "Power off (asks first)");
            Add("Open settings", s.OpenSettingsCommand, shortcut: "Settings");
            Add("USB devices", s.OpenUsbCommand);
            Add("Send Ctrl+Alt+Del", s.SendCtrlAltDelCommand, s.IsActive);
            Add("Save a screenshot", s.SaveScreenshotCommand, s.IsActive);
            Add("Clone", s.CloneCommand, s.IsOff && !s.IsTemplate);
            Add("Make into a template", s.MakeTemplateCommand, s.CanMakeTemplate);
            Add("Turn back into a machine", s.UndoTemplateCommand, s.IsTemplate);
            Add("Delete machine", s.DeleteMachineCommand, s.IsOff, "Delete the machine (asks first)");
            Add("Show in file manager", s.OpenFolderCommand);
            Add("Move to folder…", s.MoveToGroupCommand);
            Add("Take out of the folder", s.RemoveFromGroupCommand, s.HasGroup);
            Add("Copy command line", s.CopyCommandLineCommand);
            Add(s.IsFavorite ? "Remove from favorites" : "Add to favorites", s.ToggleFavoriteCommand);
            items.Add(new("Show summary", s.Name, () => s.Tab = 0) { Keys = "Ctrl+1" });
            items.Add(new("Show snapshots", s.Name, () => s.Tab = 1) { Keys = "Ctrl+2" });
            items.Add(new("Show performance", s.Name, () => s.Tab = 2) { Keys = "Ctrl+3" });
            items.Add(new("Show console", s.Name, () => s.Tab = 3) { Keys = "Ctrl+4" });
            items.Add(new("Show log", s.Name, () => s.Tab = 4) { Keys = "Ctrl+5" });
        }

        return items;
    }
}
