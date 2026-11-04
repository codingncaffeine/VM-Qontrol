using Avalonia.Controls;
using Avalonia.Input;
using VmQontrol.App.ViewModels;

namespace VmQontrol.App.Views;

/// <summary>One keyboard shortcut: the keys, what the sheet and menus call it, and when it runs.</summary>
/// <param name="Keys">Avalonia's gesture text ("Ctrl+B").</param>
/// <param name="Shown">The keys as people read them ("Ctrl+,").</param>
/// <param name="Title">What it does, as the sheet, menus and palette say it.</param>
/// <param name="Group">The sheet's section.</param>
/// <param name="CanRun">Whether it applies now (a machine selected, running...).</param>
/// <param name="Run">The action.</param>
/// <param name="WhileTyping">Runs even while a text box has the keyboard (only keys that mean nothing to a text box).</param>
public sealed record Shortcut(string Keys, string Shown, string Title, string Group, Func<ShellViewModel, bool> CanRun, Action<ShellViewModel, MainWindow> Run, bool WhileTyping = true)
{
    public KeyGesture Gesture { get; } = KeyGesture.Parse(Keys);
}

/// <summary>
/// Every keyboard shortcut, in one table: the window runs them, the shortcut sheet (Ctrl+?) lists them, the command
/// palette shows them beside its entries. The choices follow the desktop's own (Ctrl+N, Ctrl+F, Ctrl+Q, F1, F11,
/// Ctrl+,) and VMware Workstation's machine keys (Ctrl+B power on, Ctrl+E shut down, Ctrl+Z suspend, Ctrl+R reset,
/// Ctrl+D settings, Ctrl+M snapshot, Ctrl+Alt+Insert for Ctrl+Alt+Del), which people moving over already know.
/// </summary>
public static class Shortcuts
{
    private const string App = "VM Qontrol";
    private const string MachineGroup = "The selected machine";
    private const string Move = "Moving around";

    // With several machines selected, the one-machine keys stand aside: only those that make sense for many act, on all.
    private static MachineViewModel? M(ShellViewModel s) => s.ShowMany ? null : s.Selected;

    public static readonly IReadOnlyList<Shortcut> All =
    [
        new("Ctrl+N", "Ctrl+N", "New machine", App, _ => true, (s, _) => s.NewMachine()),
        new("Ctrl+Shift+N", "Ctrl+Shift+N", "New folder", App, _ => true, (s, _) => s.NewGroup()),
        new("Ctrl+O", "Ctrl+O", "Import a machine", App, _ => true, (s, _) => s.OpenImport()),
        new("Ctrl+OemComma", "Ctrl+,", "Preferences", App, _ => true, (s, _) => s.OpenPreferences()),
        new("Ctrl+F", "Ctrl+F", "Search machines", App, _ => true, (_, w) => w.FocusLater("Search")),
        new("Ctrl+K", "Ctrl+K", "All commands", App, _ => true, (s, w) => w.TogglePalette()),
        new("F5", "F5", "Look for machines again", App, _ => true, (s, w) => { _ = s.RefreshAsync(); }),
        new("F1", "F1", "Help: getting started", App, _ => true, (_, _) => OpenHelp()),
        new("Ctrl+Shift+OemQuestion", "Ctrl+?", "Keyboard shortcuts", App, _ => true, (s, _) => s.ShowShortcuts = !s.ShowShortcuts),
        new("Ctrl+OemQuestion", "Ctrl+/", "Keyboard shortcuts", App, _ => true, (s, _) => s.ShowShortcuts = !s.ShowShortcuts),
        new("F11", "F11", "Full screen", App, _ => true, (_, w) => w.WindowState = w.WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen),
        new("Ctrl+Q", "Ctrl+Q", "Quit (running machines keep running)", App, _ => true, (_, w) => w.Quit()),

        // One entry per key: with several machines selected, Ctrl+B, Ctrl+E and Delete act on all of them (Esc = back to one).
        new("Ctrl+B", "Ctrl+B", "Power on", MachineGroup,
            s => s.ShowMany ? s.PowerOnChosenCommand.CanExecute(null) : M(s) is { CanPowerOn: true },
            (s, _) => { if (s.ShowMany) s.PowerOnChosenCommand.Execute(null); else M(s)!.PowerOnCommand.Execute(null); }),
        new("Ctrl+Shift+B", "Ctrl+Shift+B", "Power on in the background", MachineGroup, s => M(s) is { CanPowerOn: true }, (s, _) => M(s)!.PowerOnInBackgroundCommand.Execute(null)),
        new("Enter", "Enter", "Power on, or open the screen of a running machine", MachineGroup, s => M(s) is { CanPowerOn: true } or { IsActive: true },
            (s, _) => (M(s)!.IsActive ? M(s)!.OpenScreenCommand : M(s)!.PowerOnCommand).Execute(null), WhileTyping: false),
        new("Ctrl+E", "Ctrl+E", "Shut down", MachineGroup,
            s => s.ShowMany ? s.ShutDownChosenCommand.CanExecute(null) : M(s) is { IsRunning: true },
            (s, _) => { if (s.ShowMany) s.ShutDownChosenCommand.Execute(null); else M(s)!.ShutDownCommand.Execute(null); }),
        new("Ctrl+Shift+P", "Ctrl+Shift+P", "Pause or resume", MachineGroup, s => M(s) is { CanPause: true } or { IsPaused: true },
            (s, _) => (M(s)!.IsPaused ? M(s)!.ResumeCommand : M(s)!.PauseCommand).Execute(null)),
        new("Ctrl+Z", "Ctrl+Z", "Suspend (save its state and turn off)", MachineGroup, s => M(s) is { IsActive: true }, (s, _) => M(s)!.SuspendCommand.Execute(null), WhileTyping: false),
        new("Ctrl+R", "Ctrl+R", "Reset (asks first)", MachineGroup, s => M(s) is { IsActive: true }, (s, _) => M(s)!.ResetCommand.Execute(null)),
        new("Ctrl+Shift+E", "Ctrl+Shift+E", "Power off (asks first)", MachineGroup, s => M(s) is { IsActive: true }, (s, _) => M(s)!.PowerOffCommand.Execute(null)),
        new("Ctrl+Alt+Insert", "Ctrl+Alt+Insert", "Send Ctrl+Alt+Del", MachineGroup, s => M(s) is { IsActive: true }, (s, _) => M(s)!.SendCtrlAltDelCommand.Execute(null)),
        new("Ctrl+D", "Ctrl+D", "Settings", MachineGroup, s => M(s) is not null, (s, _) => M(s)!.OpenSettingsCommand.Execute(null)),
        new("Ctrl+U", "Ctrl+U", "USB devices", MachineGroup, s => M(s) is not null, (s, _) => M(s)!.OpenUsbCommand.Execute(null)),
        new("Ctrl+M", "Ctrl+M", "Take a snapshot (type its name, then Enter)", MachineGroup, s => M(s) is not null, (s, w) => { M(s)!.Tab = 1; w.FocusLater("SnapshotName"); }),
        new("Ctrl+Shift+S", "Ctrl+Shift+S", "Save a screenshot", MachineGroup, s => M(s) is { IsActive: true }, (s, _) => M(s)!.SaveScreenshotCommand.Execute(null)),
        new("Delete", "Delete", "Delete the machine (asks first)", MachineGroup,
            s => s.ShowMany ? s.DeleteChosenCommand.CanExecute(null) : M(s) is { IsOff: true },
            (s, _) => { if (s.ShowMany) s.DeleteChosenCommand.Execute(null); else M(s)!.DeleteMachineCommand.Execute(null); }, WhileTyping: false),

        new("Alt+Down", "Alt+↓", "Next machine", Move, s => s.Machines.Count > 0, (s, _) => s.SelectNext(1)),
        new("Alt+Up", "Alt+↑", "Previous machine", Move, s => s.Machines.Count > 0, (s, _) => s.SelectNext(-1)),
        new("Ctrl+D1", "Ctrl+1", "Summary", Move, s => M(s) is not null, (s, _) => M(s)!.Tab = 0),
        new("Ctrl+D2", "Ctrl+2", "Snapshots", Move, s => M(s) is not null, (s, _) => M(s)!.Tab = 1),
        new("Ctrl+D3", "Ctrl+3", "Performance", Move, s => M(s) is not null, (s, _) => M(s)!.Tab = 2),
        new("Ctrl+D4", "Ctrl+4", "Console", Move, s => M(s) is not null, (s, _) => M(s)!.Tab = 3),
        new("Ctrl+D5", "Ctrl+5", "Log", Move, s => M(s) is not null, (s, _) => M(s)!.Tab = 4),
        new("Ctrl+PageDown", "Ctrl+PgDn", "Next tab", Move, s => M(s) is not null, (s, _) => M(s)!.Tab = (M(s)!.Tab + 1) % 5),
        new("Ctrl+PageUp", "Ctrl+PgUp", "Previous tab", Move, s => M(s) is not null, (s, _) => M(s)!.Tab = (M(s)!.Tab + 4) % 5),
        new("Ctrl+S", "Ctrl+S", "Save (in a machine's Settings)", Move, s => s.SettingsPage is not null, (s, _) => s.SettingsPage!.SaveCommand.Execute(null)),
    ];

    /// <summary>The sheet's sections: each action once, with every key that does it ("Ctrl+? or Ctrl+/").</summary>
    public static readonly IReadOnlyList<ShortcutSection> Sheet = All.GroupBy(s => s.Group)
        .Select(g => new ShortcutSection(g.Key, g.GroupBy(s => s.Title).Select(t => new ShortcutRow(t.Key, string.Join(" or ", t.Select(s => s.Shown)))).ToList()))
        .ToList();

    /// <summary>The keys shown beside an entry called <paramref name="title"/> (menus, palette), or <c>null</c>.</summary>
    public static string? ShownFor(string title) => All.FirstOrDefault(s => s.Title == title)?.Shown;

    /// <summary>Runs the shortcut these keys make, if one applies now. Text boxes keep their own keys.</summary>
    public static bool TryRun(ShellViewModel shell, MainWindow window, KeyEventArgs e, bool typing)
    {
        foreach (var s in All)
        {
            if (!s.Gesture.Matches(e) || (typing && !s.WhileTyping) || !s.CanRun(shell)) continue;
            s.Run(shell, window);
            return true;
        }

        return false;
    }

    public const string HelpUrl = "https://github.com/codingncaffeine/VM-Qontrol/wiki/Getting-Started";

    private static void OpenHelp() => _ = Task.Run(() =>
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open") { ArgumentList = { HelpUrl }, UseShellExecute = false });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    });
}

public sealed record ShortcutSection(string Title, IReadOnlyList<ShortcutRow> Rows);

public sealed record ShortcutRow(string Title, string Keys);
