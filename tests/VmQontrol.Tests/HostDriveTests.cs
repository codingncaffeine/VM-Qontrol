using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using VmQontrol.Core.Devices;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Media;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

public sealed class HostDriveTests
{
    private static void Block(string sys, string path, string dev, long sectors, string removable = "0", string ro = "0", bool partition = false)
    {
        var dir = Path.Combine(sys, "block", path);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dev"), dev + "\n");
        File.WriteAllText(Path.Combine(dir, "size"), sectors + "\n");
        File.WriteAllText(Path.Combine(dir, "ro"), ro + "\n");
        if (partition) File.WriteAllText(Path.Combine(dir, "partition"), "1\n");
        else File.WriteAllText(Path.Combine(dir, "removable"), removable + "\n");
    }

    [Fact]
    public void Drives_are_listed_with_their_partitions_and_what_uses_them()
    {
        using var profile = new TestProfile("drives");
        var sys = Path.Combine(profile.Root, "sys");
        var dev = Path.Combine(profile.Root, "dev");
        var proc = Path.Combine(profile.Root, "proc");
        var udev = Path.Combine(profile.Root, "udev");
        Block(sys, "nvme0n1", "259:0", 7814037168);
        Block(sys, "nvme0n1/nvme0n1p1", "259:1", 4194304, partition: true);
        Block(sys, "nvme0n1/nvme0n1p2", "259:2", 7809842864, partition: true);
        Block(sys, "sdb", "8:16", 61440000, removable: "1");
        Block(sys, "sdb/sdb1", "8:17", 61438000, partition: true);
        Block(sys, "sr0", "11:0", 0, removable: "1");
        Block(sys, "loop0", "7:0", 131072);
        Block(sys, "zram0", "252:0", 1000);
        Directory.CreateDirectory(Path.Combine(sys, "block", "sr0", "device"));
        File.WriteAllText(Path.Combine(sys, "block", "sr0", "device", "type"), "5\n");
        Directory.CreateDirectory(udev);
        File.WriteAllText(Path.Combine(udev, "b8:16"), "E:ID_BUS=usb\nE:ID_VENDOR=Kingston\nE:ID_MODEL=DataTraveler_3.0\n");
        File.WriteAllText(Path.Combine(udev, "b8:17"), "E:ID_FS_TYPE=exfat\nE:ID_FS_LABEL=PHOTOS\nE:ID_MODEL=DataTraveler_3.0\n");
        File.WriteAllText(Path.Combine(udev, "b259:0"), "E:ID_MODEL=Samsung SSD 9100 PRO 4TB\n");
        var byId = Path.Combine(dev, "disk", "by-id");
        Directory.CreateDirectory(byId);
        foreach (var n in new[] { "sdb", "sdb1", "nvme0n1", "sr0" }) File.WriteAllText(Path.Combine(dev, n), "");
        File.CreateSymbolicLink(Path.Combine(byId, "usb-Kingston_DataTraveler_3.0_1234-0:0"), "../../sdb");
        File.CreateSymbolicLink(Path.Combine(byId, "wwn-0x5000"), "../../sdb");
        File.CreateSymbolicLink(Path.Combine(byId, "usb-Kingston_DataTraveler_3.0_1234-0:0-part1"), "../../sdb1");
        Directory.CreateDirectory(Path.Combine(proc, "self"));
        File.WriteAllText(Path.Combine(proc, "self", "mountinfo"),
            "26 1 0:34 /@ / rw shared:1 - btrfs /dev/nvme0n1p2 rw,subvol=/@\n27 26 259:1 / /efi rw - vfat /dev/nvme0n1p1 rw\n90 26 0:61 / /run/media/me/PHOTOS rw - btrfs /dev/sdb1 rw\n");
        File.WriteAllText(Path.Combine(proc, "swaps"), "Filename Type Size Used Priority\n");

        var drives = HostDrives.List(sys: sys, dev: dev, proc: proc, udev: udev);
        Assert.Equal(["nvme0n1", "nvme0n1p1", "nvme0n1p2", "sdb", "sdb1", "sr0"], drives.Select(d => d.Name));

        // btrfs reports an anonymous device number (0:34): the root is found by its source device.
        Assert.Equal(["/"], drives.Single(d => d.Name == "nvme0n1p2").Mounts);
        Assert.True(drives.Single(d => d.Name == "nvme0n1p2").HoldsSystem);
        var system = drives.Single(d => d.Name == "nvme0n1");
        Assert.True(system.HoldsSystem);
        Assert.Equal("it holds this computer's own system", system.WriteRefusal);
        Assert.Equal("Samsung SSD 9100 PRO 4TB", system.Model);

        var stick = drives.Single(d => d.Name == "sdb");
        Assert.Equal("Kingston DataTraveler 3.0", stick.Model);
        Assert.Equal(Path.Combine(byId, "usb-Kingston_DataTraveler_3.0_1234-0:0"), stick.StablePath); // a readable name beats a WWN
        Assert.True(stick.Removable);
        Assert.False(stick.HoldsSystem);
        Assert.Equal(["/run/media/me/PHOTOS"], stick.Mounts); // its partition is mounted, so the disk is in use
        Assert.Contains("unmount it first", stick.WriteRefusal);
        Assert.Equal("Kingston DataTraveler 3.0 (removable disk, 29.3 GiB)", stick.Describe());

        var part = drives.Single(d => d.Name == "sdb1");
        Assert.Equal("PHOTOS on Kingston DataTraveler 3.0", part.Model);
        Assert.Equal("exfat", part.FileSystem);
        Assert.Equal("sdb", part.Parent);

        var disc = drives.Single(d => d.Name == "sr0");
        Assert.Equal(HostDriveKind.Optical, disc.Kind);
        Assert.True(disc.Empty);
        Assert.Equal("a disc drive is read-only", disc.WriteRefusal);

        // Unmounted, the stick may be written to (after the person confirms).
        File.WriteAllText(Path.Combine(proc, "self", "mountinfo"), "26 1 0:34 /@ / rw - btrfs /dev/nvme0n1p2 rw\n");
        Assert.Null(HostDrives.List(sys: sys, dev: dev, proc: proc, udev: udev).Single(d => d.Name == "sdb").WriteRefusal);
    }

    /// <summary>On this computer itself: the drive under "/" is found and never offered for writing, whatever its file system.</summary>
    [Fact]
    public void The_drive_under_this_computers_root_is_guarded()
    {
        var root = File.ReadAllLines("/proc/self/mountinfo").Select(l => l.Split(' ')).FirstOrDefault(f => f.Length > 4 && f[4] == "/");
        var dash = root is null ? -1 : Array.IndexOf(root, "-");
        Assert.SkipWhen(dash < 0 || !root![dash + 2].StartsWith("/dev/", StringComparison.Ordinal) || root[dash + 2].StartsWith("/dev/mapper/", StringComparison.Ordinal)
                        || !File.Exists(root[dash + 2]),
            "The root file system is not on a plain block device here (a container, an encrypted volume, or the kernel's /dev/root alias).");
        var drives = HostDrives.List();
        var under = Assert.Single(drives, d => d.Kind == HostDriveKind.Partition && d.Mounts.Contains("/") || d.Kind == HostDriveKind.Disk && d.Parent is null && d.Mounts.Contains("/") && !drives.Any(p => p.Parent == d.Name && p.Mounts.Contains("/")));
        Assert.True(under.HoldsSystem);
        Assert.NotNull(under.WriteRefusal);
        Assert.All(drives.Where(d => d.Mounts.Count > 0), d => Assert.NotNull(d.WriteRefusal));
    }

    /// <summary>A file this account may open is opened directly (the C library's open(2), on glibc and musl alike).</summary>
    [Fact]
    public async Task A_drive_this_account_may_open_is_opened_without_udisks()
    {
        using var profile = new TestProfile("driveopen");
        var file = Path.Combine(profile.Root, "image.raw");
        await File.WriteAllBytesAsync(file, "VMQ"u8.ToArray(), TestContext.Current.CancellationToken);
        using (var ro = await DriveAccess.OpenAsync(file, writable: false, TestContext.Current.CancellationToken))
            Assert.False(ro.IsInvalid);
        using (var rw = await DriveAccess.OpenAsync(file, writable: true, TestContext.Current.CancellationToken))
        {
            using var stream = new FileStream(rw, FileAccess.ReadWrite);
            stream.Write("ok"u8);
        }

        Assert.Equal("okQ", File.ReadAllText(file));
        var missing = await Assert.ThrowsAsync<DriveAccessException>(() => DriveAccess.OpenAsync(Path.Combine(profile.Root, "none"), false, TestContext.Current.CancellationToken));
        Assert.Contains("could not be opened", missing.Message);
    }

    [Fact]
    public void Device_ids_are_stable_and_fit_QEMU()
    {
        var id = LiveHostDrives.DeviceId("/dev/disk/by-id/usb-Kingston_DataTraveler_3.0_1234-0:0");
        Assert.Equal(id, LiveHostDrives.DeviceId("/dev/disk/by-id/usb-Kingston_DataTraveler_3.0_1234-0:0"));
        Assert.NotEqual(id, LiveHostDrives.DeviceId("/dev/disk/by-id/usb-Kingston_DataTraveler_3.0_1234-0:0-part1"));
        Assert.Matches("^hostdrive-[0-9a-f]{12}$", id);
    }

    [Fact]
    public void A_machine_with_host_drives_has_the_hot_plug_controller_even_when_imported()
    {
        var m = new MachineConfig { Name = "imported", ImportedFrom = new ImportSource("quickemu", "/x"), Disks = [new DiskConfig { File = "d.qcow2" }] };
        var ctx = new Core.Qemu.LaunchContext { Host = RecordedHost.Load(), MachineDirectory = "/m", Runtime = new Core.Qemu.RuntimePaths("/r"), Headless = true };
        Assert.DoesNotContain("qemu-xhci,id=hostpass", Core.Qemu.QemuCommandBuilder.Build(m, ctx).Args);
        m.HostDrives.Add(new HostDriveConfig { Path = "/dev/disk/by-id/usb-x" });
        Assert.Contains("qemu-xhci,id=hostpass", Core.Qemu.QemuCommandBuilder.Build(m, ctx).Args);
    }

    /// <summary>A loop device over a scratch file, set up through udisks2 (allowed for the person at the computer, no password).</summary>
    internal sealed class LoopDrive : IDisposable
    {
        private LoopDrive(string file, string device)
        {
            File_ = file;
            Device = device;
        }

        public string File_ { get; }

        public string Device { get; }

        public static LoopDrive? Create(string file, byte[] firstSector)
        {
            using (var f = File.Create(file))
            {
                f.SetLength(16 << 20);
                f.Write(firstSector);
            }

            var (code, output) = Run("udisksctl", "loop-setup", "-f", file, "--no-user-interaction");
            var at = output.IndexOf("/dev/loop", StringComparison.Ordinal);
            if (code != 0 || at < 0) return null;
            return new LoopDrive(file, output[at..].TrimEnd('.', '\n', ' '));
        }

        public void Dispose() => Run("udisksctl", "loop-delete", "-b", Device, "--no-user-interaction");

        private static (int Code, string Output) Run(string program, params string[] args)
        {
            try
            {
                var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in args) start.ArgumentList.Add(a);
                using var p = Process.Start(start)!;
                var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                return (p.ExitCode, text);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return (-1, "");
            }
        }
    }

    private static MachineConfig Bare(string name) => new()
    {
        Name = name, MemoryMiB = 256, Cpu = new CpuConfig { Cores = 1 }, Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios },
        Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }],
    };

    /// <summary>
    /// A host drive this account may not open (a loop device belongs to root and the disk group) reaches QEMU through
    /// udisks2, plugs in at power on read-only, unplugs and plugs back writable while the machine runs, and keeps the
    /// machine from being suspended. QEMU's own lists are the proof.
    /// </summary>
    [Trait("Tier", "Integration")]
    [Fact]
    public async Task A_host_drive_connects_at_power_on_and_comes_and_goes_live()
    {
        var ct = TestContext.Current.CancellationToken;
        using var profile = new TestProfile("hostdrive");
        using var loop = LoopDrive.Create(Path.Combine(profile.Root, "drive.img"), "VMQ-HOSTDRIVE-01"u8.ToArray());
        Assert.SkipWhen(loop is null, "udisks2 cannot set up a loop device here (no udisks2, or not the person at the computer).");
        Assert.False(File.Exists(loop.Device) && CanOpen(loop.Device), "control: this account must NOT be able to open the loop device itself, or udisks2 is not tested");
        var drive = Assert.Single(HostDrives.List(includeLoop: true), d => d.DevicePath == loop.Device);

        var dir = Path.Combine(profile.Paths.DefaultLibrary, "hd");
        Directory.CreateDirectory(dir);
        await QemuImg.CreateAsync(Path.Combine(dir, "disk0.qcow2"), "qcow2", 1, ct: ct);
        var m = Bare("hd");
        m.HostDrives.Add(new HostDriveConfig { Path = drive.StablePath, Name = drive.Describe() });
        m.HostDrives.Add(new HostDriveConfig { Path = "/dev/disk/by-id/usb-Not_Plugged_In-0:0", Name = "A stick that is not here" });
        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct);
        try
        {
            Assert.Equal(["A stick that is not here is not connected to this computer."], running.Notices);
            Assert.Equal("running", await MachineRunner.StatusAsync(running, ct)); // it waited stopped for the drive, then ran
            // The control socket serves one client at a time: each step below opens and closes its own connection.
            await using (var qmp = await MachineRunner.ControlAsync(running, ct))
            {
                Assert.Equal([new ConnectedHostDrive(drive.StablePath, false)], await LiveHostDrives.ListAsync(qmp, ct));
                var node = await NodeAsync(qmp, ct);
                Assert.True(node?["ro"]?.GetValue<bool>());
                Assert.Equal("host_device", node?["drv"]?.GetValue<string>());
            }

            var suspend = await Assert.ThrowsAsync<QmpException>(() => MachineSuspend.SuspendAsync(running, ct: ct));
            Assert.Contains("cannot be suspended", suspend.Description);
            Assert.Equal("running", await MachineRunner.StatusAsync(running, ct));

            await using (var qmp = await MachineRunner.ControlAsync(running, ct))
            {
                await LiveHostDrives.DetachAsync(qmp, drive.StablePath, ct);
                Assert.Empty(await LiveHostDrives.ListAsync(qmp, ct));
                Assert.Null(await NodeAsync(qmp, ct));

                await LiveHostDrives.AttachAsync(qmp, new HostDriveConfig { Path = drive.StablePath, Writable = true }, ct);
                Assert.Equal([new ConnectedHostDrive(drive.StablePath, true)], await LiveHostDrives.ListAsync(qmp, ct));
                Assert.False((await NodeAsync(qmp, ct))?["ro"]?.GetValue<bool>());
                await LiveHostDrives.DetachAsync(qmp, drive.StablePath, ct);
            }
        }
        finally
        {
            await MachineRunner.PowerOffAsync(running, CancellationToken.None);
            await MachineRunner.WaitForExitAsync(running, TimeSpan.FromSeconds(10), CancellationToken.None);
        }
    }

    /// <summary>
    /// The guest sees the host drive as a disk: it reads the host's first sector and writes what it saw into the second,
    /// and the host reads that back from the file under the loop device.
    /// </summary>
    [Trait("Tier", "Guest")]
    [Fact]
    public async Task The_guest_reads_and_writes_a_host_drive()
    {
        var ct = TestContext.Current.CancellationToken;
        var image = Path.Combine(TestProfile.ScratchRoot, "images", GuestIntegrationTests.Image);
        Assert.SkipWhen(!File.Exists(image), "Run tools/fetch-test-image.sh first.");
        using var profile = new TestProfile("hostdriveguest");
        using var loop = LoopDrive.Create(Path.Combine(profile.Root, "drive.img"), "VMQ-HOSTDRIVE-01"u8.ToArray());
        Assert.SkipWhen(loop is null, "udisks2 cannot set up a loop device here.");
        var drive = Assert.Single(HostDrives.List(includeLoop: true), d => d.DevicePath == loop.Device);
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "alpine");
        Directory.CreateDirectory(dir);
        await QemuImg.RunAsync(ct, "create", "-q", "-f", "qcow2", "-b", image, "-F", "qcow2", Path.Combine(dir, "disk0.qcow2"));
        IsoWriter.Write(Path.Combine(dir, "seed.iso"), "cidata", new Dictionary<string, byte[]>
        {
            ["meta-data"] = "instance-id: vmq-hostdrive-test\n"u8.ToArray(),
            ["user-data"] = Encoding.UTF8.GetBytes("""
                #cloud-config
                runcmd:
                  - modprobe -a xhci_pci usb_storage uas sd_mod 2>/dev/null
                  - for i in $(seq 60); do [ -b /dev/sda ] && break; sleep 1; done
                  - (dd if=/dev/sda bs=16 count=1 2>/dev/null; printf ' seen by the guest') > /tmp/seen
                  - dd if=/tmp/seen of=/dev/sda bs=512 seek=1 conv=notrunc,sync 2>/dev/null
                  - sync
                  - poweroff
                """),
        });
        var m = new MachineConfig
        {
            Name = "alpine", MemoryMiB = 512, Cpu = new CpuConfig { Cores = 1 }, Firmware = new FirmwareConfig { Kind = FirmwareKind.Bios },
            Disks = [new DiskConfig { File = "disk0.qcow2" }],
            Media = [new MediaConfig { Role = MediaRole.Drivers, File = "seed.iso" }],
            HostDrives = [new HostDriveConfig { Path = drive.StablePath, Name = "loop", Writable = true }],
        };
        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct);
        try
        {
            Assert.Empty(running.Notices);
            Assert.True(await MachineRunner.WaitForExitAsync(running, TimeSpan.FromMinutes(3), ct), "the guest did not finish and power off");
        }
        finally
        {
            if (MachineRunner.Find(m, profile.Paths) is { } still) await MachineRunner.PowerOffAsync(still, CancellationToken.None);
        }

        var back = new byte[34];
        using (var f = File.OpenRead(loop.File_))
        {
            f.Position = 512;
            f.ReadExactly(back);
        }

        Assert.Equal("VMQ-HOSTDRIVE-01 seen by the guest", Encoding.ASCII.GetString(back));
    }

    private static async Task<JsonObject?> NodeAsync(QmpClient qmp, CancellationToken ct)
    {
        var nodes = await qmp.ExecuteAsync("query-named-block-nodes", new JsonObject { ["flat"] = true }, ct) as JsonArray;
        return nodes?.OfType<JsonObject>().FirstOrDefault(n => n["node-name"]?.GetValue<string>()?.StartsWith("hd", StringComparison.Ordinal) == true
                                                              && n["file"]?.GetValue<string>()?.StartsWith("/dev/fdset/", StringComparison.Ordinal) == true);
    }

    private static bool CanOpen(string path)
    {
        try
        {
            using var f = File.OpenHandle(path, FileMode.Open, FileAccess.Read);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
