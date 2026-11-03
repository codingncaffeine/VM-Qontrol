using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using VmQontrol.Core.Import;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>Phase 11 (D30): machines from VMware, VirtualBox, OVF/OVA and libvirt, and bare disks, read and copied in; the source never changes.</summary>
public sealed class ImportTests
{
    private static async Task<string> Disk(string path, string format, string? subformat = null, byte pattern = 0xab)
    {
        var ct = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await QemuImg.RunAsync(ct, ["create", "-q", "-f", format, .. subformat is null ? Array.Empty<string>() : ["-o", $"subformat={subformat}"], path, "64M"]);
        if (subformat != "streamOptimized") Io(path, format, $"write -P 0x{pattern:x2} 0 1M");
        return path;
    }

    private static string Io(string path, string format, string command)
    {
        var start = new ProcessStartInfo("qemu-io") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-f", format, "-c", command, path }) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return output;
    }

    private static string Hash(string dir) => Convert.ToHexString(SHA256.HashData(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).SelectMany(f => File.ReadAllBytes(f).Concat(System.Text.Encoding.UTF8.GetBytes(f))).ToArray()));

    private static async Task<(MachineConfig Machine, string Dir)> Import(ForeignMachine f, TestProfile profile) =>
        await MachineImporter.ImportAsync(f, profile.Paths.DefaultLibrary, new Progress<ImportProgress>(_ => { }), TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_VMware_machine_keeps_its_hardware_follows_its_snapshot_chain_and_is_left_as_it_was()
    {
        using var profile = new TestProfile("import-vmware");
        var src = Path.Combine(profile.Root, "Win 11 VM");
        var parent = await Disk(Path.Combine(src, "Win 11.vmdk"), "vmdk");
        // A snapshot: the machine now runs on a child that holds a later write.
        var child = Path.Combine(src, "Win 11-000001.vmdk");
        await QemuImg.RunAsync(TestContext.Current.CancellationToken, "create", "-q", "-f", "vmdk", "-b", parent, "-F", "vmdk", child);
        Io(child, "vmdk", "write -P 0xcd 2M 1M");
        File.WriteAllText(Path.Combine(src, "Win 11.vmx"), """
            .encoding = "UTF-8"
            displayName = "Win 11 VM"
            guestOS = "windows11-64"
            firmware = "efi"
            uefi.secureBoot.enabled = "TRUE"
            memsize = "8192"
            numvcpus = "4"
            vtpm.present = "TRUE"
            nvme0.present = "TRUE"
            nvme0:0.present = "TRUE"
            nvme0:0.fileName = "Win 11-000001.vmdk"
            sata0.present = "TRUE"
            sata0:1.present = "TRUE"
            sata0:1.deviceType = "cdrom-raw"
            ethernet0.present = "TRUE"
            ethernet0.virtualDev = "vmxnet3"
            ethernet0.connectionType = "bridged"
            ethernet0.generatedAddress = "00:0C:29:AB:CD:EF"
            """);
        var before = Hash(src);

        var f = MachineImporter.Read(Path.Combine(src, "Win 11.vmx"), profile.Paths.DefaultLibrary);
        Assert.Equal(new GuestConfig { Os = "windows", Release = "11" }, f.Guest);
        Assert.Equal((8192L, 4, FirmwareKind.Uefi, true, true), (f.MemoryMiB!.Value, f.Cores!.Value, f.Firmware, f.SecureBoot, f.Tpm));
        Assert.Equal(DiskBus.Nvme, f.Disks.Single().Bus);
        Assert.Equal(("vmxnet3", "00:0c:29:ab:cd:ef"), (f.Nics[0].Model, f.Nics[0].Mac));
        Assert.Contains(f.Notes, n => n.Contains("bridged", StringComparison.Ordinal));
        Assert.Contains(f.Notes, n => n.Contains("BitLocker", StringComparison.Ordinal));

        var (m, dir) = await Import(f, profile);
        Assert.Equal(before, Hash(src));
        var disk = Path.Combine(dir, m.Disks[0].File);
        // Both the parent's data and the snapshot's later write are in the one copy.
        Assert.Contains("read 1048576/1048576", Io(disk, "qcow2", "read -P 0xab 0 1M"), StringComparison.Ordinal);
        Assert.Contains("read 1048576/1048576", Io(disk, "qcow2", "read -P 0xcd 2M 1M"), StringComparison.Ordinal);
        Assert.Equal("qcow2", (await QemuImg.InfoAsync(disk, TestContext.Current.CancellationToken))["format"]!.GetValue<string>());
        var saved = MachineStore.Load(Path.Combine(dir, MachineStore.FileName));
        Assert.Equal(DiskBus.Nvme, saved.Disks[0].Bus);
        Assert.Equal(new ImportSource("VMware", Path.Combine(src, "Win 11.vmx")), saved.CopiedFrom);
        Assert.Null(saved.ImportedFrom);
        Assert.Contains("Imported from VMware", saved.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_VirtualBox_machine_is_read_from_its_media_registry_and_its_snapshots_stop_the_import_with_what_to_do()
    {
        using var profile = new TestProfile("import-vbox");
        var src = Path.Combine(profile.Root, "Ubuntu");
        await Disk(Path.Combine(src, "Ubuntu.vdi"), "vdi");
        string Vbox(string attached, string extra = "") => $"""
            <?xml version="1.0"?>
            <VirtualBox xmlns="http://www.virtualbox.org/" version="1.19-linux">
              <Machine uuid="m1" name="Ubuntu Box" OSType="Ubuntu_64">
                <MediaRegistry><HardDisks>
                  <HardDisk uuid="d1" location="Ubuntu.vdi" format="VDI" type="Normal">{extra}</HardDisk>
                </HardDisks></MediaRegistry>
                <Hardware>
                  <CPU count="2"/><Memory RAMSize="4096"/><Firmware type="EFI"/>
                  <Network><Adapter slot="0" enabled="true" MACAddress="080027A1B2C3" type="virtio"><NAT/></Adapter></Network>
                  <StorageControllers><StorageController name="SATA" type="AHCI">
                    <AttachedDevice type="HardDisk" port="0" device="0"><Image uuid="{attached}"/></AttachedDevice>
                  </StorageController></StorageControllers>
                </Hardware>
              </Machine>
            </VirtualBox>
            """;
        File.WriteAllText(Path.Combine(src, "Ubuntu.vbox"), Vbox("d1"));
        var f = MachineImporter.Read(Path.Combine(src, "Ubuntu.vbox"), profile.Paths.DefaultLibrary);
        Assert.Equal(("Ubuntu Box", "linux", 4096L, 2, FirmwareKind.Uefi), (f.Name, f.Guest.Os, f.MemoryMiB!.Value, f.Cores!.Value, f.Firmware));
        Assert.Equal(("virtio-net-pci", "08:00:27:a1:b2:c3"), (f.Nics[0].Model, f.Nics[0].Mac));
        Assert.Empty(f.Problems);
        var (m, dir) = await Import(f, profile);
        Assert.Contains("read 1048576/1048576", Io(Path.Combine(dir, m.Disks[0].File), "qcow2", "read -P 0xab 0 1M"), StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(src, "Ubuntu.vbox"), Vbox("d2", """<HardDisk uuid="d2" location="Snapshots/d2.vdi" format="VDI"/>"""));
        var snap = MachineImporter.Read(Path.Combine(src, "Ubuntu.vbox"), profile.Paths.DefaultLibrary);
        Assert.Contains(snap.Problems, p => p.Contains("delete the snapshots", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Import(snap, profile));
    }

    [Fact]
    public async Task An_ova_is_unpacked_inside_the_library_read_and_cleaned_up()
    {
        using var profile = new TestProfile("import-ova");
        var work = Path.Combine(profile.Root, "ova");
        var disk = await Disk(Path.Combine(work, "disk1.vmdk"), "vmdk", "streamOptimized");
        File.WriteAllText(Path.Combine(work, "app.ovf"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <Envelope xmlns="http://schemas.dmtf.org/ovf/envelope/1" xmlns:ovf="http://schemas.dmtf.org/ovf/envelope/1"
                      xmlns:rasd="http://schemas.dmtf.org/wbem/wscim/1/cim-schema/2/CIM_ResourceAllocationSettingData"
                      xmlns:vmw="http://www.vmware.com/schema/ovf">
              <References><File ovf:id="file1" ovf:href="disk1.vmdk"/></References>
              <DiskSection><Disk ovf:diskId="vmdisk1" ovf:fileRef="file1" ovf:capacity="64"/></DiskSection>
              <VirtualSystem ovf:id="appliance">
                <Name>Debian appliance</Name>
                <OperatingSystemSection ovf:id="96" vmw:osType="debian12_64Guest"/>
                <VirtualHardwareSection>
                  <Item><rasd:InstanceID>1</rasd:InstanceID><rasd:ResourceType>3</rasd:ResourceType><rasd:VirtualQuantity>2</rasd:VirtualQuantity></Item>
                  <Item><rasd:AllocationUnits>byte * 2^20</rasd:AllocationUnits><rasd:InstanceID>2</rasd:InstanceID><rasd:ResourceType>4</rasd:ResourceType><rasd:VirtualQuantity>2048</rasd:VirtualQuantity></Item>
                  <Item><rasd:InstanceID>3</rasd:InstanceID><rasd:ResourceSubType>VirtualSCSI</rasd:ResourceSubType><rasd:ResourceType>6</rasd:ResourceType></Item>
                  <Item><rasd:HostResource>ovf:/disk/vmdisk1</rasd:HostResource><rasd:InstanceID>4</rasd:InstanceID><rasd:Parent>3</rasd:Parent><rasd:ResourceType>17</rasd:ResourceType></Item>
                  <Item><rasd:Address>00:50:56:12:34:56</rasd:Address><rasd:InstanceID>5</rasd:InstanceID><rasd:ResourceSubType>VmxNet3</rasd:ResourceSubType><rasd:ResourceType>10</rasd:ResourceType></Item>
                  <vmw:Config ovf:required="false" vmw:key="firmware" vmw:value="efi"/>
                </VirtualHardwareSection>
              </VirtualSystem>
            </Envelope>
            """);
        var ova = Path.Combine(profile.Root, "Debian.ova");
        await using (var tar = File.Create(ova))
        await using (var writer = new TarWriter(tar))
        {
            await writer.WriteEntryAsync(Path.Combine(work, "app.ovf"), "app.ovf", TestContext.Current.CancellationToken);
            await writer.WriteEntryAsync(disk, "disk1.vmdk", TestContext.Current.CancellationToken);
        }

        var f = MachineImporter.Read(ova, profile.Paths.DefaultLibrary);
        Assert.Equal(("Debian appliance", "linux", 2048L, 2, FirmwareKind.Uefi, DiskBus.Pvscsi), (f.Name, f.Guest.Os, f.MemoryMiB!.Value, f.Cores!.Value, f.Firmware, f.Disks[0].Bus));
        Assert.Equal(("vmxnet3", "00:50:56:12:34:56"), (f.Nics[0].Model, f.Nics[0].Mac));
        Assert.StartsWith(Path.Combine(profile.Paths.DefaultLibrary, ".importing"), f.TemporaryFolder, StringComparison.Ordinal);
        Assert.Equal(ova, f.Source);
        var (m, _) = await Import(f, profile);
        Assert.False(Directory.Exists(f.TemporaryFolder));
        Assert.Equal(64, (await QemuImg.InfoAsync(Path.Combine(profile.Paths.DefaultLibrary, m.Name, m.Disks[0].File), TestContext.Current.CancellationToken))["virtual-size"]!.GetValue<long>() >> 20);
    }

    [Fact]
    public async Task A_libvirt_domain_maps_its_disks_and_names_the_system_by_its_libosinfo_id()
    {
        using var profile = new TestProfile("import-libvirt");
        var disk = await Disk(Path.Combine(profile.Root, "images", "fedora.qcow2"), "qcow2");
        var xml = $"""
            <domain type="kvm">
              <name>fedora40</name>
              <metadata><libosinfo:libosinfo xmlns:libosinfo="http://libosinfo.org/xmlns/libvirt/domain/1.0"><libosinfo:os id="http://fedoraproject.org/fedora/40"/></libosinfo:libosinfo></metadata>
              <memory unit="KiB">4194304</memory>
              <vcpu placement="static">3</vcpu>
              <os firmware="efi"><type arch="x86_64" machine="pc-q35-9.0">hvm</type></os>
              <devices>
                <disk type="file" device="disk"><driver name="qemu" type="qcow2"/><source file="{disk}"/><target dev="vda" bus="virtio"/></disk>
                <interface type="network"><mac address="52:54:00:11:22:33"/><source network="default"/><model type="virtio"/></interface>
                <tpm model="tpm-crb"><backend type="emulator" version="2.0"/></tpm>
              </devices>
            </domain>
            """;
        File.WriteAllText(Path.Combine(profile.Root, "fedora40.xml"), xml);
        var f = MachineImporter.Read(Path.Combine(profile.Root, "fedora40.xml"), profile.Paths.DefaultLibrary);
        Assert.Equal(("fedora40", "linux", "http://fedoraproject.org/fedora/40", 4096L, 3, FirmwareKind.Uefi, true), (f.Name, f.Guest.Os, f.Guest.OsInfoId, f.MemoryMiB!.Value, f.Cores!.Value, f.Firmware, f.Tpm));
        Assert.Equal(DiskBus.Virtio, f.Disks.Single().Bus);
        Assert.Equal(("virtio-net-pci", "52:54:00:11:22:33"), (f.Nics[0].Model, f.Nics[0].Mac));
        var (m, dir) = await Import(f, profile);
        Assert.Contains("read 1048576/1048576", Io(Path.Combine(dir, m.Disks[0].File), "qcow2", "read -P 0xab 0 1M"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bare_disk_asks_for_the_system_and_a_failed_copy_leaves_no_half_machine()
    {
        using var profile = new TestProfile("import-disk");
        var f = MachineImporter.Read(Path.Combine(profile.Root, "missing.vhdx"), profile.Paths.DefaultLibrary);
        Assert.Equal(("disk", FirmwareKind.Uefi), (f.Kind, f.Firmware));
        Assert.Contains(f.Notes, n => n.Contains("check the system", StringComparison.Ordinal));
        // A disk that is not there is refused before anything is made (audit S1 checks every named file first).
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => MachineImporter.ImportAsync(f, profile.Paths.DefaultLibrary, new Progress<ImportProgress>(_ => { }), TestContext.Current.CancellationToken));
        Assert.Contains("is not a file", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(profile.Paths.DefaultLibrary, "missing")));
    }

    [Theory]
    [InlineData("windows9-64", "windows", "10")]
    [InlineData("windows11-64", "windows", "11")]
    [InlineData("Windows10_64", "windows", "10")]
    [InlineData("windows2019srv-64", "windows-server", "")]
    [InlineData("windows11_64Guest", "windows", "11")]
    [InlineData("http://microsoft.com/win/10", "windows", "10")]
    [InlineData("ubuntu-64", "linux", "")]
    [InlineData("freebsd13-64", "freebsd", "")]
    [InlineData("darwin23-64", "macos", "")]
    public void Systems_are_named_from_every_source_s_identifiers(string id, string os, string release)
    {
        var g = ForeignGuest.From(id);
        Assert.Equal((os, release), (g.Os, g.Release));
    }
}
