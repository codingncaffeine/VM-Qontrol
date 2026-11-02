using VmQontrol.Core.Install;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Media;
using VmQontrol.Core.Qemu;

namespace VmQontrol.Tests;

public sealed class LinuxEasyInstallTests
{
    private static readonly LinuxInstallChoices Choices = new() { UserName = "Ann", Password = "s3cret", HostName = "lab-box", TimeZone = "America/Phoenix" };

    [Fact]
    public void Ubuntu_autoinstall_carries_the_account_hashed_and_the_agents()
    {
        var yaml = LinuxEasyInstall.UbuntuAutoinstall(Choices, Sha512Crypt.Hash("s3cret", "salt"));
        Assert.StartsWith("#cloud-config\nautoinstall:", yaml, StringComparison.Ordinal);
        Assert.Contains("username: ann", yaml, StringComparison.Ordinal);
        Assert.Contains("password: \"$6$salt$", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", yaml, StringComparison.Ordinal);
        Assert.Contains("- qemu-guest-agent", yaml, StringComparison.Ordinal);
        Assert.Contains("timezone: America/Phoenix", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Kickstart_and_preseed_never_carry_the_password_in_clear()
    {
        var hash = Sha512Crypt.Hash("s3cret", "salt");
        var ks = LinuxEasyInstall.Kickstart(Choices, hash);
        var preseed = LinuxEasyInstall.Preseed(Choices, hash);
        Assert.Contains("user --name=ann", ks, StringComparison.Ordinal);
        Assert.Contains("--iscrypted --password=$6$salt$", ks, StringComparison.Ordinal);
        Assert.Contains("d-i passwd/user-password-crypted password $6$salt$", preseed, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", ks + preseed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("My Lab Box!", "my-lab-box")]
    [InlineData("Ubuntu 24.04", "ubuntu-24-04")]
    [InlineData("!!!", "vm")]
    public void Host_names_follow_the_rules(string machine, string host) => Assert.Equal(host, LinuxEasyInstall.HostName(machine));

    [Fact]
    public void A_Fedora_family_disc_gets_a_kickstart_volume_labelled_OEMDRV_and_no_kernel_boot()
    {
        using var profile = new TestProfile("ks");
        var iso = Path.Combine(profile.Root, "fedora.iso");
        IsoWriter.Write(iso, "Fedora-S-dvd-x86_64-41", new Dictionary<string, byte[]> { [".treeinfo"] = "[general]\n"u8.ToArray() }, keepLabelCase: true);
        Assert.Equal(LinuxInstaller.Anaconda, LinuxEasyInstall.Detect(iso));
        var (media, boot) = LinuxEasyInstall.Prepare(LinuxInstaller.Anaconda, iso, profile.Root, Choices);
        Assert.Null(boot);
        Assert.Equal("OEMDRV", IsoVolume.Read(Path.Combine(profile.Root, media.Single().File))!.VolumeId);
        using var ks = IsoFiles.Open(Path.Combine(profile.Root, "kickstart.iso"))!;
        Assert.Contains("autopart", System.Text.Encoding.UTF8.GetString(ks.Read("ks.cfg")!), StringComparison.Ordinal);
    }

    [Fact]
    public void A_direct_kernel_boot_is_used_only_until_the_system_is_installed()
    {
        var m = new MachineConfig
        {
            Name = "u",
            Disks = [new DiskConfig { File = "disk0.qcow2" }],
            KernelBoot = new KernelBootConfig { Kernel = "installer-vmlinuz", Initrd = "installer-initrd", Append = "autoinstall ds=nocloud quiet ---" },
        };
        LaunchContext Ctx(bool used) => new() { Host = RecordedHost.Load(), MachineDirectory = "/vms/u", Runtime = new RuntimePaths("/run/u"), SystemDiskUsed = used, FileExists = _ => false, FirmwareFormat = _ => "raw" };
        var installing = QemuCommandBuilder.Build(m, Ctx(false)).Args.ToList();
        Assert.Equal("/vms/u/installer-vmlinuz", installing[installing.IndexOf("-kernel") + 1]);
        Assert.Equal("autoinstall ds=nocloud quiet ---", installing[installing.IndexOf("-append") + 1]);
        Assert.DoesNotContain("-kernel", QemuCommandBuilder.Build(m, Ctx(true)).Args);
    }
}
