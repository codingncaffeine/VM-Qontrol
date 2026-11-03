using System.Windows.Input;
using VmQontrol.App.ViewModels;

namespace VmQontrol.App.Platform;

/// <summary>One line of the tray's menu: a command, a separator (<c>Header = null</c>), or a machine with its own items.</summary>
public sealed record TrayEntry(string? Header, ICommand? Command = null, bool Enabled = true, IReadOnlyList<TrayEntry>? Items = null)
{
    public static readonly TrayEntry Separator = new((string?)null);
}

/// <summary>
/// What the tray icon's menu holds (Workstation's tray lists the running machines): show the window, each machine with the
/// actions its state allows (running ones first, then favorites, then the rest, at most <see cref="MostMachines"/>), quit.
/// A plain model, so tests read it without a desktop tray.
/// </summary>
public static class TrayMenu
{
    public const int MostMachines = 12;

    public static IReadOnlyList<TrayEntry> Build(ShellViewModel shell, ICommand show, ICommand quit)
    {
        var entries = new List<TrayEntry> { new("Show VM Qontrol", show), TrayEntry.Separator };
        var machines = shell.Machines.Where(m => !m.IsTemplate)
            .OrderByDescending(m => m.IsActive).ThenByDescending(m => m.IsFavorite).ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        foreach (var m in machines.Take(MostMachines))
        {
            var items = m.IsActive
                ? new List<TrayEntry>
                {
                    new("Open screen", m.OpenScreenCommand),
                    new("Shut down", m.ShutDownCommand, m.IsRunning),
                    new("Suspend", m.SuspendCommand),
                }
                : [new(m.PowerOnText, m.PowerOnInBackgroundCommand, m.CanPowerOn)];
            entries.Add(new($"{(m.IsActive ? "● " : "")}{m.Name} — {m.StateText}", Items: items));
        }

        if (machines.Count > MostMachines) entries.Add(new($"{machines.Count - MostMachines} more in the window…", show));
        if (machines.Count == 0) entries.Add(new("No machines yet", Enabled: false));
        entries.Add(TrayEntry.Separator);
        entries.Add(new("Quit VM Qontrol", quit));
        return entries;
    }

    /// <summary>The tooltip: how many machines run.</summary>
    public static string Tip(ShellViewModel shell)
    {
        var running = shell.Machines.Count(m => m.IsActive);
        return running == 0 ? "VM Qontrol: no machine running" : $"VM Qontrol: {running} machine{(running == 1 ? "" : "s")} running";
    }
}
