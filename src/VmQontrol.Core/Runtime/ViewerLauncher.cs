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
        return Path.GetFileName(program) == "spicy"
            ? (program, ["--uri=" + uri, "--title", m.Name])
            : (program, [uri, "--title", m.Name, "--auto-resize=always"]);
    }

    /// <summary>Starts the viewer and returns its program name, or <c>null</c> when none is installed.</summary>
    public static string? Open(MachineConfig m, RunningMachine running)
    {
        if (Command(m, running) is not { } command) return null;
        var start = new ProcessStartInfo(command.Program) { UseShellExecute = false };
        foreach (var a in command.Args) start.ArgumentList.Add(a);
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
