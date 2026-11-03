using System.Text;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Import;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Media;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>
/// Phase 11's acceptance: a VMware machine and a libvirt machine import and BOOT. Built from the Alpine test image (as
/// a .vmdk behind a .vmx, and a qcow2 behind a domain XML); the guest powers itself off at first boot (cloud-init), which
/// it can only do once the imported disk has started on the hardware the import chose.
/// </summary>
[Trait("Tier", "Guest")]
public sealed class ImportBootTests
{
    private static void Seed(string iso) => IsoWriter.Write(iso, "cidata", new Dictionary<string, byte[]>
    {
        ["meta-data"] = "instance-id: vmq-import\nlocal-hostname: vmq-import\n"u8.ToArray(),
        ["user-data"] = Encoding.UTF8.GetBytes("#cloud-config\nruncmd:\n  - poweroff\n"),
    });

    private static async Task BootsAndPowersOff(ForeignMachine f, TestProfile profile)
    {
        var ct = TestContext.Current.CancellationToken;
        var (m, dir) = await MachineImporter.ImportAsync(f, profile.Paths.DefaultLibrary, new Progress<ImportProgress>(_ => { }), ct);
        m = m with { MemoryMiB = 512, Cpu = new CpuConfig { Cores = 1 } };
        var running = await MachineRunner.StartAsync(m, dir, profile.Paths, HostProbe.Probe(), new StartOptions { Headless = true }, ct);
        try
        {
            Assert.True(await MachineRunner.WaitForExitAsync(running, TimeSpan.FromMinutes(3), ct), $"the imported {f.Kind} machine did not boot and power itself off");
        }
        finally
        {
            if (MachineRunner.Find(m, profile.Paths) is { } still) await MachineRunner.PowerOffAsync(still, CancellationToken.None);
        }
    }

    private static string? Image()
    {
        var image = Path.Combine(TestProfile.ScratchRoot, "images", GuestIntegrationTests.Image);
        return File.Exists(image) ? image : null;
    }

    [Fact]
    public async Task A_VMware_machine_imports_and_boots()
    {
        var image = Image();
        Assert.SkipWhen(image is null, "Run tools/fetch-test-image.sh first.");
        using var profile = new TestProfile("import-boot-vmware");
        var src = Path.Combine(profile.Root, "Alpine VM");
        Directory.CreateDirectory(src);
        await QemuImg.RunAsync(TestContext.Current.CancellationToken, "convert", "-q", "-O", "vmdk", image!, Path.Combine(src, "Alpine.vmdk"));
        Seed(Path.Combine(src, "seed.iso"));
        File.WriteAllText(Path.Combine(src, "Alpine.vmx"), """
            displayName = "Alpine VM"
            guestOS = "other5xlinux-64"
            memsize = "512"
            numvcpus = "1"
            sata0.present = "TRUE"
            sata0:0.present = "TRUE"
            sata0:0.fileName = "Alpine.vmdk"
            sata0:1.present = "TRUE"
            sata0:1.deviceType = "cdrom-image"
            sata0:1.fileName = "seed.iso"
            ethernet0.present = "TRUE"
            ethernet0.virtualDev = "e1000"
            """);
        await BootsAndPowersOff(MachineImporter.Read(Path.Combine(src, "Alpine.vmx"), profile.Paths.DefaultLibrary), profile);
    }

    [Fact]
    public async Task A_libvirt_machine_imports_and_boots()
    {
        var image = Image();
        Assert.SkipWhen(image is null, "Run tools/fetch-test-image.sh first.");
        using var profile = new TestProfile("import-boot-libvirt");
        var disk = Path.Combine(profile.Root, "alpine.qcow2");
        File.Copy(image!, disk);
        var seed = Path.Combine(profile.Root, "seed.iso");
        Seed(seed);
        var xml = $"""
            <domain type="kvm">
              <name>alpine</name>
              <memory unit="MiB">512</memory>
              <vcpu>1</vcpu>
              <os><type arch="x86_64" machine="q35">hvm</type></os>
              <devices>
                <disk type="file" device="disk"><driver name="qemu" type="qcow2"/><source file="{disk}"/><target dev="vda" bus="virtio"/></disk>
                <disk type="file" device="cdrom"><source file="{seed}"/><target dev="sda" bus="sata"/></disk>
                <interface type="network"><model type="virtio"/></interface>
              </devices>
            </domain>
            """;
        await BootsAndPowersOff(LibvirtReader.Read(xml, "test"), profile);
    }

    [Fact]
    public async Task A_libvirt_machine_on_virtio_scsi_imports_and_boots_from_it()
    {
        var image = Image();
        Assert.SkipWhen(image is null, "Run tools/fetch-test-image.sh first.");
        using var profile = new TestProfile("import-boot-vscsi");
        var disk = Path.Combine(profile.Root, "alpine.qcow2");
        File.Copy(image!, disk);
        var seed = Path.Combine(profile.Root, "seed.iso");
        Seed(seed);
        var xml = $"""
            <domain type="kvm">
              <name>alpine</name>
              <memory unit="MiB">512</memory>
              <vcpu>1</vcpu>
              <os><type arch="x86_64" machine="q35">hvm</type></os>
              <devices>
                <controller type="scsi" index="0" model="virtio-scsi"/>
                <disk type="file" device="disk"><driver name="qemu" type="qcow2"/><source file="{disk}"/><target dev="sda" bus="scsi"/></disk>
                <disk type="file" device="cdrom"><source file="{seed}"/><target dev="sdb" bus="sata"/></disk>
              </devices>
            </domain>
            """;
        var f = LibvirtReader.Read(xml, "test");
        Assert.Equal(DiskBus.Scsi, f.Disks[0].Bus);
        await BootsAndPowersOff(f, profile);
    }
}
