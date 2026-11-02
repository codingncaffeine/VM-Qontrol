using VmQontrol.Core.Machines;
using VmQontrol.Core.Qemu;

namespace VmQontrol.Tests;

public sealed class CommandBuilderTests
{
    private static QemuLaunch Build(MachineConfig m, bool headless = false, bool diskUsed = false) =>
        QemuCommandBuilder.Build(m, new LaunchContext
        {
            Host = RecordedHost.Load(),
            MachineDirectory = "/vms/m",
            Runtime = new RuntimePaths("/run/vmq/m"),
            Headless = headless,
            SystemDiskUsed = diskUsed,
            FileExists = _ => false,
            FirmwareFormat = _ => "raw",
        });

    private static MachineConfig Linux => new() { Name = "Test", Disks = [new DiskConfig { File = "disk0.qcow2" }], Media = [new MediaConfig { Role = MediaRole.Installer, File = "i.iso", Connect = Connect.UntilInstalled }] };

    [Fact]
    public void A_headless_launch_is_silent_and_has_no_screen()
    {
        var args = Build(Linux, headless: true).Args.ToList();
        Assert.Equal("none,id=audio0", args[args.IndexOf("-audiodev") + 1]);
        Assert.Equal("none", args[args.IndexOf("-display") + 1]);
        Assert.DoesNotContain("-spice", args);
        Assert.DoesNotContain(args, a => a.Contains("usbredir", StringComparison.Ordinal));
    }

    [Fact]
    public void A_normal_launch_sends_sound_through_spice_never_straight_to_the_host()
    {
        var args = Build(Linux).Args.ToList();
        Assert.Equal("spice,id=audio0", args[args.IndexOf("-audiodev") + 1]);
    }

    [Fact]
    public void Guest_isolation_turns_the_clipboard_and_dropped_files_off_in_the_spice_server()
    {
        static string Spice(MachineConfig m) { var a = Build(m).Args.ToList(); return a[a.IndexOf("-spice") + 1]; }
        Assert.DoesNotContain("disable-copy-paste", Spice(Linux));
        Assert.DoesNotContain("disable-agent-file-xfer", Spice(Linux));
        var isolated = Spice(Linux with { Display = Linux.Display with { CopyPaste = false, FileTransfer = false } });
        Assert.Contains(",disable-copy-paste=on", isolated);
        Assert.Contains(",disable-agent-file-xfer=on", isolated);
    }

    [Fact]
    public void The_machine_is_controlled_over_QMP_with_the_human_monitor_off()
    {
        var args = Build(Linux).Args.ToList();
        Assert.Equal("none", args[args.IndexOf("-monitor") + 1]);
        Assert.Equal("unix:/run/vmq/m/qmp.sock,server=on,wait=off", args[args.IndexOf("-qmp") + 1]);
    }

    [Fact]
    public void The_installer_leaves_the_drive_once_the_system_is_installed()
    {
        Assert.Contains("media=cdrom,index=0,file=/vms/m/i.iso", Build(Linux).Args);
        Assert.DoesNotContain(Build(Linux, diskUsed: true).Args, a => a.Contains("i.iso", StringComparison.Ordinal));
    }

    [Fact]
    public void Usb_devices_are_passed_through_as_quickemu_writes_them()
    {
        var m = Linux with { Usb = new UsbConfig { Passthrough = [new UsbDevice("046d", "c52b")] } };
        var args = Build(m).Args;
        Assert.Contains("qemu-xhci,id=hostpass", args);
        Assert.Contains("usb-host,bus=hostpass.0,vendorid=0x046d,productid=0xc52b,id=usbauto-046d-c52b", args);
    }

    [Fact]
    public void A_name_with_spaces_and_commas_is_escaped_for_qemu()
    {
        var args = Build(Linux with { Name = "My VM, test" }).Args.ToList();
        Assert.Equal("My VM,, test,process=my-vm-test,debug-threads=on", args[args.IndexOf("-name") + 1]);
    }

    [Fact]
    public void Missing_secure_boot_firmware_is_an_error_that_says_what_to_install()
    {
        var m = Linux with { Firmware = new FirmwareConfig { SecureBoot = true } };
        var launch = QemuCommandBuilder.Build(m, new LaunchContext
        {
            Host = RecordedHost.Load() with { FirmwareFiles = new HashSet<string> { "/usr/share/OVMF/OVMF_CODE.fd", "/usr/share/OVMF/OVMF_VARS.fd" } },
            MachineDirectory = "/vms/m",
            Runtime = new RuntimePaths("/run/vmq/m"),
            FileExists = _ => false,
        });
        Assert.Contains(launch.Errors, e => e.Contains("Secure Boot", StringComparison.Ordinal) && e.Contains("OVMF", StringComparison.Ordinal));
    }

    [Fact]
    public void Windows_on_too_little_memory_is_a_warning_when_the_user_chose_it()
    {
        var m = Linux with { Guest = new GuestConfig { Os = "windows" }, MemoryMiB = 2048 };
        Assert.Contains(Build(m).Warnings, w => w.Contains("4 GB", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_KVM_the_machine_still_builds_with_a_plain_warning()
    {
        var launch = QemuCommandBuilder.Build(Linux, new LaunchContext
        {
            Host = RecordedHost.Load() with { KvmUsable = false },
            MachineDirectory = "/vms/m",
            Runtime = new RuntimePaths("/run/vmq/m"),
            FileExists = _ => false,
            FirmwareFormat = _ => "raw",
        });
        Assert.Contains("-accel", launch.Args);
        Assert.Contains(launch.Warnings, w => w.Contains("KVM", StringComparison.Ordinal));
    }

    [Fact]
    public void Boot_and_device_options_reach_the_command_and_their_defaults_add_nothing()
    {
        var plain = Build(Linux).Args.ToList();
        Assert.DoesNotContain("-boot", plain);
        Assert.Contains("virtio-rng-pci,rng=rng0", plain);

        var m = Linux with
        {
            Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios },
            Devices = new DevicesConfig { Rng = false, Balloon = false, Watchdog = "reset", CrashReporting = true, BootOrder = "dc", BootMenu = true },
        };
        var args = Build(m).Args.ToList();
        Assert.Equal("order=dc,menu=on,splash-time=5000", args[args.IndexOf("-boot") + 1]);
        Assert.DoesNotContain("virtio-rng-pci,rng=rng0", args);
        Assert.DoesNotContain("virtio-balloon", args);
        Assert.Contains("i6300esb", args);
        Assert.Equal("watchdog=reset", args[args.IndexOf("-action") + 1]);
        Assert.Contains("pvpanic", args);
    }

    [Fact]
    public void A_UEFI_machine_ignores_a_BIOS_boot_order()
    {
        var args = Build(Linux with { Devices = new DevicesConfig { BootOrder = "dc" } }).Args;
        Assert.DoesNotContain("-boot", args);
    }

    [Fact]
    public void Two_machines_on_the_real_network_never_share_a_MAC()
    {
        MachineConfig Bridged(string id) => Linux with { Id = id, Nics = [new NicConfig { Mode = NetMode.Bridge, Bridge = "br0" }] };
        string Nic(MachineConfig m) { var a = Build(m).Args.ToList(); return a[a.IndexOf("-nic") + 1]; }
        var original = Nic(Bridged("aaaa"));
        var clone = Nic(Bridged("bbbb"));
        Assert.Contains(",mac=52:54:00:", original, StringComparison.Ordinal);
        Assert.NotEqual(original, clone);
        Assert.Equal(original, Nic(Bridged("aaaa")));
        // A quickemu machine keeps quickemu's command line.
        Assert.DoesNotContain("mac=", Nic(Bridged("cccc") with { ImportedFrom = new ImportSource("quickemu", "/x.conf") }), StringComparison.Ordinal);
    }
}
