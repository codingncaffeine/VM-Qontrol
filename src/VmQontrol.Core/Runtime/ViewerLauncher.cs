using System.Diagnostics;
using VmQontrol.Core.Machines;

namespace VmQontrol.Core.Runtime;

/// <summary>Opens an external SPICE viewer on a running machine's screen.</summary>
public static class ViewerLauncher
{
    /// <summary>The viewers VM Qontrol knows, in order of preference when the machine says "auto".</summary>
    public static readonly string[] Known = ["remote-viewer", "spicy"];

    public static string? Find(string choice)
    {
        foreach (var name in choice == "auto" ? Known : [choice])
        {
            if (OnPath(name) is { } path) return path;
        }

        return null;
    }

    /// <summary>The viewer's command line for a machine, or <c>null</c> when no viewer is installed.</summary>
    public static (string Program, List<string> Args)? Command(MachineConfig m, RunningMachine running)
    {
        if (Find(m.Display.Viewer) is not { } program) return null;
        var uri = $"spice+unix://{running.Paths.Spice}";
        // Both viewers take -f/--full-screen (spicy 0.42, remote-viewer 11).
        List<string> full = m.Display.FullScreen ? ["--full-screen"] : [];
        return Path.GetFileName(program) == "spicy"
            ? (program, ["--uri=" + uri, "--title", m.Name, .. full])
            : (program, [uri, "--title", m.Name, "--auto-resize=always", .. full]);
    }

    /// <summary>Starts the viewer and returns its program name, or <c>null</c> when none is installed.</summary>
    /// <remarks>remote-viewer's icon buttons get tooltips through <see cref="ViewerTooltips"/>, written under <paramref name="paths"/>' cache.</remarks>
    public static string? Open(MachineConfig m, RunningMachine running, QontrolPaths? paths = null)
    {
        if (Command(m, running) is not { } command) return null;
        var start = new ProcessStartInfo(command.Program) { UseShellExecute = false };
        foreach (var a in command.Args) start.ArgumentList.Add(a);
        if (paths is not null && Path.GetFileName(command.Program) == "remote-viewer" && ViewerTooltips.Overlay(command.Program, paths.Cache) is { } overlay)
        {
            // Kept with any overlays the person set themselves (GLib reads a colon-separated list).
            var own = Environment.GetEnvironmentVariable(ViewerTooltips.Variable);
            start.Environment[ViewerTooltips.Variable] = string.IsNullOrEmpty(own) ? overlay : $"{overlay}:{own}";
        }
        using var _ = Process.Start(start);
        return Path.GetFileName(command.Program);
    }

    private static string? OnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
