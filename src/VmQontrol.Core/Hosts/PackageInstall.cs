using System.Diagnostics;

namespace VmQontrol.Core.Hosts;

/// <summary>
/// Installs one of the distribution's packages VM Qontrol can use, from a button, the way a software center does
/// without one: the distribution's own package manager run through polkit (pkexec), so the person types their password
/// once in the desktop's own prompt and never opens a terminal. Only the package names from <see cref="Doctor"/>'s
/// table are ever installed (nothing typed or received goes into the command). A package VM Qontrol's own package
/// already depends on never needs this; it is for a build or a tarball run as is.
/// </summary>
public static class PackageInstall
{
    /// <summary>The command pkexec runs (no shell): non-interactive, only what is missing.</summary>
    public static IReadOnlyList<string>? Command(DistroFamily distro, string packages)
    {
        var names = packages.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (names.Length == 0 || names.Any(n => !n.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '+' or '_'))) return null;
        IReadOnlyList<string>? head = distro switch
        {
            DistroFamily.Arch => ["/usr/bin/pacman", "-S", "--needed", "--noconfirm"],
            DistroFamily.Debian => ["/usr/bin/env", "DEBIAN_FRONTEND=noninteractive", "apt-get", "install", "-y"],
            DistroFamily.Fedora => ["/usr/bin/dnf", "install", "-y"],
            DistroFamily.Alpine => ["/sbin/apk", "add"],
            DistroFamily.OpenSuse => ["/usr/bin/zypper", "--non-interactive", "install"],
            _ => null,
        };
        return head is null ? null : [.. head, .. names];
    }

    /// <summary>Whether a button can install <paramref name="component"/> here.</summary>
    public static bool CanInstall(string component) =>
        File.Exists("/usr/bin/pkexec") && Doctor.PackageNames(Doctor.DetectThisDistro(), component) is { } pkg && Command(Doctor.DetectThisDistro(), pkg) is not null;

    /// <summary>Installs it; null when done, else what went wrong in words.</summary>
    public static async Task<string?> InstallAsync(string component, CancellationToken ct = default)
    {
        var distro = Doctor.DetectThisDistro();
        if (!File.Exists("/usr/bin/pkexec")) return "polkit's pkexec is not installed, so the password cannot be asked for. " + Doctor.InstallCommand(distro, component);
        if (Doctor.PackageNames(distro, component) is not { } pkg || Command(distro, pkg) is not { } command)
            return Doctor.InstallCommand(distro, component);
        var start = new ProcessStartInfo("/usr/bin/pkexec") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in command) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        var output = p.StandardOutput.ReadToEndAsync(ct);
        var error = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await output.ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        Log.Info($"Installed {pkg} through pkexec: exit {p.ExitCode}");
        return p.ExitCode switch
        {
            0 => null,
            126 => "The password prompt was closed, so nothing was installed.",
            127 => "polkit refused.",
            _ => $"The package manager stopped ({error.Trim().Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? $"exit {p.ExitCode}"}).",
        };
    }
}
