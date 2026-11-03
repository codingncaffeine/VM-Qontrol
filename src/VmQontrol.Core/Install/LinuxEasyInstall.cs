using System.Text;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Media;

namespace VmQontrol.Core.Install;

/// <summary>What the person chose for an unattended Linux installation.</summary>
public sealed record LinuxInstallChoices
{
    public required string UserName { get; init; }

    /// <summary>Required: the installers do not make an account without one.</summary>
    public required string Password { get; init; }

    public required string HostName { get; init; }

    /// <summary>An IANA time zone, e.g. "America/Phoenix".</summary>
    public string TimeZone { get; init; } = "UTC";

    /// <summary>e.g. "en_US.UTF-8".</summary>
    public string Locale { get; init; } = "en_US.UTF-8";

    /// <summary>An X keyboard layout, e.g. "us".</summary>
    public string Keyboard { get; init; } = "us";

    /// <summary>Power off when the installation is done (tests), instead of restarting into the new system.</summary>
    public bool PowerOffWhenDone { get; init; }
}

/// <summary>The installer families Linux Easy Install knows.</summary>
public enum LinuxInstaller
{
    None,

    /// <summary>Ubuntu's Subiquity: autoinstall from a "cidata" seed, started with the "autoinstall" kernel option.</summary>
    Ubuntu,

    /// <summary>Anaconda (Fedora, RHEL, Rocky, Alma): a ks.cfg kickstart on a volume labeled OEMDRV is read on its own.</summary>
    Anaconda,

    /// <summary>debian-installer: preseed.cfg inside the initrd is read on its own.</summary>
    Debian,
}

/// <summary>
/// Linux Easy Install, the way virt-install does it: each installer gets its answer file where it looks by itself
/// (a seed disc, a labeled volume, or inside its initrd) and, where it needs an option, its own kernel booted directly
/// with that option added. Everything is made in the machine's folder; the disc is only read.
/// </summary>
public static class LinuxEasyInstall
{
    /// <summary>Which installer is on the disc, from the files it carries.</summary>
    public static LinuxInstaller Detect(string iso)
    {
        using var files = IsoFiles.Open(iso);
        if (files is null) return LinuxInstaller.None;
        if (files.Exists("casper/vmlinuz") && (files.Exists("casper/initrd") || files.Exists("casper/initrd.gz"))) return LinuxInstaller.Ubuntu;
        if (files.Exists("install.amd/vmlinuz") && files.Exists("install.amd/initrd.gz")) return LinuxInstaller.Debian;
        if (files.Exists("images/pxeboot/vmlinuz") || files.Exists(".treeinfo")) return LinuxInstaller.Anaconda;
        return LinuxInstaller.None;
    }

    /// <summary>Makes the answer media in <paramref name="machineDirectory"/> and returns what the machine must attach.</summary>
    public static (List<MediaConfig> Media, KernelBootConfig? KernelBoot) Prepare(LinuxInstaller installer, string iso, string machineDirectory, LinuxInstallChoices c)
    {
        var hash = Sha512Crypt.Hash(c.Password);
        switch (installer)
        {
            case LinuxInstaller.Ubuntu:
            {
                IsoWriter.Write(Path.Combine(machineDirectory, "autoinstall.iso"), "cidata", new Dictionary<string, byte[]>
                {
                    ["user-data"] = Encoding.UTF8.GetBytes(UbuntuAutoinstall(c, hash)),
                    ["meta-data"] = Encoding.UTF8.GetBytes($"instance-id: {c.HostName}\n"),
                });
                using var files = IsoFiles.Open(iso) ?? throw new InvalidDataException($"{iso} is not a disc image.");
                var initrdName = files.Exists("casper/initrd") ? "casper/initrd" : "casper/initrd.gz";
                File.WriteAllBytes(Path.Combine(machineDirectory, "installer-vmlinuz"), files.Read("casper/vmlinuz")!);
                File.WriteAllBytes(Path.Combine(machineDirectory, "installer-initrd"), files.Read(initrdName)!);
                return ([new MediaConfig { Role = MediaRole.Unattended, File = "autoinstall.iso", Connect = Connect.UntilInstalled }],
                    new KernelBootConfig { Kernel = "installer-vmlinuz", Initrd = "installer-initrd", Append = "autoinstall ds=nocloud quiet ---" });
            }

            case LinuxInstaller.Anaconda:
                IsoWriter.Write(Path.Combine(machineDirectory, "kickstart.iso"), "OEMDRV", new Dictionary<string, byte[]>
                {
                    ["ks.cfg"] = Encoding.UTF8.GetBytes(Kickstart(c, hash)),
                }, keepLabelCase: true);
                return ([new MediaConfig { Role = MediaRole.Unattended, File = "kickstart.iso", Connect = Connect.UntilInstalled }], null);

            case LinuxInstaller.Debian:
            {
                using var files = IsoFiles.Open(iso) ?? throw new InvalidDataException($"{iso} is not a disc image.");
                File.WriteAllBytes(Path.Combine(machineDirectory, "installer-vmlinuz"), files.Read("install.amd/vmlinuz")!);
                // The disc's initrd with preseed.cfg added as a second archive; the kernel unpacks both.
                var initrd = files.Read("install.amd/initrd.gz")!;
                var addition = Cpio.Gzipped(new Dictionary<string, byte[]> { ["preseed.cfg"] = Encoding.UTF8.GetBytes(Preseed(c, hash)) });
                File.WriteAllBytes(Path.Combine(machineDirectory, "installer-initrd"), [.. initrd, .. addition]);
                return ([], new KernelBootConfig { Kernel = "installer-vmlinuz", Initrd = "installer-initrd", Append = "auto=true priority=critical quiet" });
            }

            default:
                throw new InvalidOperationException("This disc's installer is not one Easy Install knows.");
        }
    }

    public static string UbuntuAutoinstall(LinuxInstallChoices c, string hash) => $"""
        #cloud-config
        autoinstall:
          version: 1
          locale: {c.Locale}
          keyboard:
            layout: {c.Keyboard}
          timezone: {c.TimeZone}
          identity:
            hostname: {c.HostName}
            username: {AccountName(c.UserName)}
            realname: "{c.UserName.Replace("\"", "", StringComparison.Ordinal)}"
            password: "{hash}"
          ssh:
            install-server: true
            allow-pw: true
          storage:
            layout:
              name: direct
          packages:
            - qemu-guest-agent
            - spice-vdagent
          late-commands:
            - sh -c '{InstallSignal.LinuxCommand}'
          shutdown: {(c.PowerOffWhenDone ? "poweroff" : "reboot")}

        """;

    public static string Kickstart(LinuxInstallChoices c, string hash) => $"""
        # VM Qontrol Easy Install
        text
        lang {c.Locale}
        keyboard {c.Keyboard}
        timezone {c.TimeZone} --utc
        network --bootproto=dhcp --hostname={c.HostName}
        rootpw --lock
        user --name={AccountName(c.UserName)} --gecos="{c.UserName.Replace("\"", "", StringComparison.Ordinal)}" --groups=wheel --iscrypted --password={hash}
        zerombr
        clearpart --all --initlabel
        autopart
        bootloader
        firstboot --disable
        %packages
        qemu-guest-agent
        spice-vdagent
        %end
        %post --nochroot
        {InstallSignal.LinuxCommand}
        %end
        {(c.PowerOffWhenDone ? "poweroff" : "reboot")}

        """;

    public static string Preseed(LinuxInstallChoices c, string hash) => $"""
        d-i debian-installer/locale string {c.Locale.Split('.')[0]}
        d-i keyboard-configuration/xkb-keymap select {c.Keyboard}
        d-i netcfg/choose_interface select auto
        d-i netcfg/get_hostname string {c.HostName}
        d-i netcfg/get_domain string local
        d-i mirror/country string manual
        d-i mirror/http/hostname string deb.debian.org
        d-i mirror/http/directory string /debian
        d-i mirror/http/proxy string
        d-i passwd/root-login boolean false
        d-i passwd/user-fullname string {c.UserName}
        d-i passwd/username string {AccountName(c.UserName)}
        d-i passwd/user-password-crypted password {hash}
        d-i clock-setup/utc boolean true
        d-i time/zone string {c.TimeZone}
        d-i partman-auto/method string regular
        d-i partman-auto/choose_recipe select atomic
        d-i partman-partitioning/confirm_write_new_label boolean true
        d-i partman/choose_partition select finish
        d-i partman/confirm boolean true
        d-i partman/confirm_nooverwrite boolean true
        d-i partman-efi/non_efi_system boolean true
        d-i apt-setup/cdrom/set-first boolean false
        tasksel tasksel/first multiselect standard, ssh-server
        d-i pkgsel/include string qemu-guest-agent spice-vdagent
        d-i pkgsel/upgrade select none
        popularity-contest popularity-contest/participate boolean false
        d-i grub-installer/only_debian boolean true
        d-i grub-installer/bootdev string default
        d-i preseed/late_command string {InstallSignal.LinuxCommand}
        d-i finish-install/reboot_in_progress note
        d-i debian-installer/exit/poweroff boolean {(c.PowerOffWhenDone ? "true" : "false")}

        """;

    /// <summary>
    /// The account name made from the person's name, in the form every installer accepts: lower-case letters, digits,
    /// '-' and '_', starting with a letter, at most 32 ("Jane Doe" becomes "janedoe").
    /// </summary>
    public static string AccountName(string userName)
    {
        var name = new string(userName.ToLowerInvariant().Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_').ToArray()).TrimStart('-', '_');
        if (name.Length == 0) return "user";
        if (!char.IsAsciiLetter(name[0])) name = "u" + name;
        return name[..Math.Min(32, name.Length)];
    }

    /// <summary>A Linux host name from a machine name: lower case letters, digits and hyphens, at most 63.</summary>
    public static string HostName(string machineName)
    {
        var sb = new StringBuilder();
        foreach (var ch in machineName.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }

        var name = sb.ToString().Trim('-');
        return name.Length == 0 ? "vm" : name[..Math.Min(63, name.Length)].TrimEnd('-');
    }
}
