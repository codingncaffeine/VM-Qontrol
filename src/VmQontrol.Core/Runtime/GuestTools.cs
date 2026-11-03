using VmQontrol.Core.Machines;

namespace VmQontrol.Core.Runtime;

/// <summary>How the guest tools get into a machine: the disc and the steps for Windows, the one command for Linux.</summary>
/// <param name="Summary">One sentence: what the tools bring.</param>
/// <param name="Steps">What to do inside the machine, in order.</param>
/// <param name="Command">The command to type in the machine's terminal (Linux, BSD), or <c>null</c>.</param>
/// <param name="NeedsDriversDisc">The steps run from the VirtIO driver disc (virtio-win.iso), which goes in the machine's drive.</param>
/// <param name="After">What to do once <paramref name="Command"/> has run.</param>
public sealed record GuestToolsAdvice(string Summary, IReadOnlyList<string> Steps, string? Command, bool NeedsDriversDisc, IReadOnlyList<string>? After = null)
{
    public IReadOnlyList<string> AfterSteps => After ?? [];
}

public static class GuestTools
{
    /// <summary>Where the VirtIO driver disc comes from (the virtio-win project's stable build).</summary>
    public const string DriversDiscPage = "https://github.com/virtio-win/virtio-win-pkg-scripts/blob/master/README.md";

    private const string Brings = "The guest tools bring copy and paste, dragging files in, a screen that follows the window's size, the machine's address here, and a clean shut down from this app.";

    /// <summary>The steps for <paramref name="guest"/>; <paramref name="osReleaseId"/> is the guest's own os-release ID when known (it overrides the guess from the catalog).</summary>
    public static GuestToolsAdvice Advise(GuestConfig guest, string? osReleaseId = null)
    {
        switch (guest.Os)
        {
            case "windows" or "windows-server":
                return new GuestToolsAdvice(Brings,
                [
                    "The VirtIO driver disc goes in the machine's drive.",
                    "In the machine, open File Explorer → This PC → the CD drive \"virtio-win\".",
                    "Right-click virtio-win-guest-tools.exe, choose Run as administrator, and accept its steps until it says it has finished (it installs the drivers, the guest agent and the SPICE agent).",
                    "Restart Windows, even if it does not ask.",
                    "To check: Device Manager → Display adapters shows \"Red Hat QXL controller\" (resolutions and copy and paste then work). If it still says Microsoft Basic Display Adapter, run the installer again and choose Repair.",
                ], null, NeedsDriversDisc: true);
            case "macos":
                return new GuestToolsAdvice("macOS has no SPICE guest tools: copy and paste and the guest agent are not available. Shared folders and the network work without them.", [], null, false);
            case "freebsd":
                return new GuestToolsAdvice(Brings, ["Open a terminal in the machine as root and run:"],
                    "pkg install -y qemu-guest-agent && sysrc qemu_guest_agent_enable=YES && service qemu-guest-agent start", false, ["Restart the machine."]);
            case "linux":
                break;
            default:
                return new GuestToolsAdvice("This system has no known guest tools. The machine works without them; copy and paste and the guest's address are not available.", [], null, false);
        }

        var command = LinuxCommand(osReleaseId ?? DistroFromOsInfo(guest.OsInfoId));
        return new GuestToolsAdvice(Brings,
            ["Open a terminal in the machine and run:"], command, false,
            ["Log out and back in (or restart) so the desktop picks up copy and paste."]);
    }

    /// <summary>"http://ubuntu.com/ubuntu/24.04" → "ubuntu"; the distribution name in an osinfo-db identifier.</summary>
    public static string? DistroFromOsInfo(string? osInfoId)
    {
        if (osInfoId is null || !Uri.TryCreate(osInfoId, UriKind.Absolute, out var uri)) return null;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0].ToLowerInvariant() : null;
    }

    /// <summary>The install command for a Linux distribution, by its os-release ID (or osinfo-db family).</summary>
    public static string LinuxCommand(string? distro)
    {
        const string Packages = "qemu-guest-agent spice-vdagent";
        const string Enable = "sudo systemctl enable --now qemu-guest-agent";
        return distro switch
        {
            "debian" or "ubuntu" or "linuxmint" or "mint" or "pop" or "elementary" or "zorin" or "kali" or "neon" or "kubuntu" or "xubuntu" or "lubuntu" or "ubuntu-mate" or "ubuntu-budgie" or "mx" or "devuan" or "trisquel"
                => $"sudo apt install -y {Packages} && {Enable}",
            "fedora" or "rhel" or "centos" or "centos-stream" or "rocky" or "almalinux" or "alma" or "ol" or "nobara" or "ultramarine"
                => $"sudo dnf install -y {Packages} && {Enable}",
            "arch" or "archlinux" or "manjaro" or "endeavouros" or "cachyos" or "garuda" or "artix"
                => $"sudo pacman -S --needed {Packages} && {Enable}",
            "opensuse" or "opensuse-tumbleweed" or "opensuse-leap" or "sles" or "suse"
                => $"sudo zypper install -y {Packages} && {Enable}",
            "alpine" or "alpinelinux"
                => $"doas apk add {Packages} && doas rc-update add qemu-guest-agent && doas rc-service qemu-guest-agent start",
            "void" or "voidlinux"
                => $"sudo xbps-install -y {Packages} && sudo ln -s /etc/sv/qemu-ga /var/service/",
            "gentoo"
                => "sudo emerge app-emulation/qemu-guest-agent app-emulation/spice-vdagent && sudo rc-update add qemu-guest-agent default",
            "nixos"
                => "Add to configuration.nix: services.qemuGuest.enable = true; services.spice-vdagentd.enable = true; then: sudo nixos-rebuild switch",
            _ => $"Install the packages {Packages} with the system's package manager, then: {Enable}",
        };
    }

    /// <summary>The VirtIO driver disc this machine already holds, if any.</summary>
    public static string? MachineDriversDisc(MachineConfig m, string machineDirectory)
    {
        var file = m.Media.FirstOrDefault(x => x.Role == MediaRole.Drivers)?.File;
        if (file is null) return null;
        var path = Path.IsPathRooted(file) ? file : Path.Combine(machineDirectory, file);
        return File.Exists(path) ? path : null;
    }
}
