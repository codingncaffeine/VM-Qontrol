using System.Diagnostics;

namespace VmQontrol.App.Platform;

/// <summary>
/// Runs a command in the person's own terminal: xdg-terminal-exec (the freedesktop default-terminal standard) when the
/// desktop has it, then $TERMINAL, then the desktops' own terminals, each with the way it takes a command to run.
/// </summary>
internal static class TerminalLauncher
{
    /// <summary>Terminals in the order they are tried, and the arguments that run a command in each.</summary>
    private static readonly (string Program, string[] Before)[] Known =
    [
        ("xdg-terminal-exec", []),
        ("konsole", ["-e"]),
        ("gnome-terminal", ["--"]),
        ("kgx", ["--"]),
        ("ptyxis", ["--"]),
        ("xfce4-terminal", ["-x"]),
        ("mate-terminal", ["-x"]),
        ("lxterminal", ["-e"]),
        ("tilix", ["-e"]),
        ("alacritty", ["-e"]),
        ("kitty", []),
        ("foot", []),
        ("wezterm", ["start", "--"]),
        ("xterm", ["-e"]),
    ];

    /// <summary>The program and arguments that run <paramref name="command"/>, or <c>null</c> when no terminal is found.</summary>
    internal static (string Program, List<string> Args)? For(IReadOnlyList<string> command, Func<string, string?> find, string? terminalVariable)
    {
        if (find("xdg-terminal-exec") is { } xdg) return (xdg, [.. command]);
        if (terminalVariable is { Length: > 0 } t && find(t) is { } chosen)
        {
            var known = Known.FirstOrDefault(k => k.Program == Path.GetFileName(t));
            return (chosen, [.. known.Program is null ? ["-e"] : known.Before, .. command]);
        }

        foreach (var (program, before) in Known.Skip(1))
            if (find(program) is { } path) return (path, [.. before, .. command]);
        return null;
    }

    /// <summary>Starts the terminal; <c>false</c> when this computer has none VM Qontrol knows.</summary>
    public static bool Run(IReadOnlyList<string> command)
    {
        if (For(command, Which, Environment.GetEnvironmentVariable("TERMINAL")) is not { } launch) return false;
        var start = new ProcessStartInfo(launch.Program) { UseShellExecute = false };
        foreach (var a in launch.Args) start.ArgumentList.Add(a);
        try
        {
            using var _ = Process.Start(start);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string? Which(string program)
    {
        if (program.Contains('/')) return File.Exists(program) ? program : null;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin").Split(':'))
        {
            var full = Path.Combine(dir, program);
            if (File.Exists(full)) return full;
        }

        return null;
    }
}
