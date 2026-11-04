using System.Diagnostics;
using VmQontrol.Core.Import;
using VmQontrol.Core.Machines;

namespace VmQontrol.Tests;

/// <summary>
/// A disk with no settings is recognized from its partition table: real disks partitioned by sfdisk (the util-linux
/// tool installers use), converted by qemu-img to the formats people bring, read back through qemu-io.
/// </summary>
public sealed class DiskProbeTests
{
    private static string Sfdisk => File.Exists("/usr/bin/sfdisk") ? "/usr/bin/sfdisk" : "/sbin/sfdisk";

    private static void Run(string program, string input, params string[] args)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        p.StandardInput.Write(input);
        p.StandardInput.Close();
        var error = p.StandardError.ReadToEnd();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{program}: {error}");
    }

    /// <summary>A 64 MB disk with these partitions (an sfdisk script), as <paramref name="format"/>.</summary>
    private static string Disk(TestProfile profile, string name, string layout, string format)
    {
        var raw = Path.Combine(profile.Root, name + ".raw");
        using (var f = File.Create(raw)) f.SetLength(64L << 20);
        if (layout.Length > 0) Run(Sfdisk, layout, "-q", raw);
        if (format == "raw") return raw;
        var target = Path.Combine(profile.Root, name + "." + (format == "vpc" ? "vhd" : format));
        Run("qemu-img", "", "convert", "-O", format, raw, target);
        return target;
    }

    private static void SkipWithoutTools() =>
        Assert.SkipWhen(!File.Exists(Sfdisk) || !File.Exists("/usr/bin/qemu-io"), "sfdisk and qemu-io are needed (util-linux, qemu-img)");

    private const string WindowsGpt = "label: gpt\nsize=8M, type=C12A7328-F81F-11D2-BA4B-00A0C93EC93B\nsize=8M, type=E3C9E316-0B5C-4DB8-817D-F92DF00215AE\nsize=30M, type=EBD0A0A2-B9E5-4433-87C0-68B6B72699C7\ntype=DE94BBA4-06D1-4D40-A16A-BFD50179D6AC\n";

    [Theory]
    [InlineData("vmdk")]
    [InlineData("vhdx")]
    [InlineData("vdi")]
    [InlineData("qcow2")]
    public void A_windows_disk_is_windows_with_uefi_in_every_format(string format)
    {
        SkipWithoutTools();
        using var profile = new TestProfile("probe-win");
        var facts = DiskProbe.Probe(Disk(profile, "win", WindowsGpt, format));
        Assert.NotNull(facts);
        Assert.Equal(("gpt", true, "windows"), (facts.Table, facts.Uefi, facts.Os));
        Assert.Contains("Microsoft reserved", facts.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Linux_on_gpt_with_a_bios_boot_partition_starts_with_bios_and_linux_on_mbr_too()
    {
        SkipWithoutTools();
        using var profile = new TestProfile("probe-linux");
        var bios = DiskProbe.Probe(Disk(profile, "grub", "label: gpt\nsize=1M, type=21686148-6449-6E6F-744E-656564454649\ntype=0FC63DAF-8483-4772-8E79-3D69D8477DE4\n", "qcow2"))!;
        Assert.Equal(("gpt", false, "linux"), (bios.Table, bios.Uefi, bios.Os));
        var mbr = DiskProbe.Probe(Disk(profile, "old", "label: dos\nsize=48M, type=83, bootable\ntype=82\n", "vmdk"))!;
        Assert.Equal(("mbr", false, "linux"), (mbr.Table, mbr.Uefi, mbr.Os));
        var ntfs = DiskProbe.Probe(Disk(profile, "win7", "label: dos\nsize=1M, type=7, bootable\ntype=7\n", "vpc"))!;
        Assert.Equal(("mbr", false, "windows"), (ntfs.Table, ntfs.Uefi, ntfs.Os));
        var empty = DiskProbe.Probe(Disk(profile, "empty", "", "qcow2"))!;
        Assert.Null(empty.Table);
        Assert.Null(empty.Os);
    }

    [Fact]
    public void A_bare_disk_import_takes_what_the_disk_says()
    {
        SkipWithoutTools();
        using var profile = new TestProfile("probe-import");
        var win = MachineImporter.BareDisk(Disk(profile, "Office PC", WindowsGpt, "vmdk"));
        Assert.Equal(("windows", "11", FirmwareKind.Uefi, true), (win.Guest.Os, win.Guest.Release, win.Firmware, win.Tpm));
        Assert.Contains("Recognized from the disk", win.Notes.Single(), StringComparison.Ordinal);
        var linux = MachineImporter.BareDisk(Disk(profile, "server", "label: dos\ntype=83, bootable\n", "qcow2"));
        Assert.Equal(("linux", FirmwareKind.Bios, false), (linux.Guest.Os, linux.Firmware, linux.Tpm));
    }

    /// <summary>A Hyper-V export: the .vmcx is not read, its disk in Virtual Hard Disks/ is, and recognized.</summary>
    [Fact]
    public void A_hyper_v_export_brings_its_disk_recognized()
    {
        SkipWithoutTools();
        using var profile = new TestProfile("probe-hyperv");
        var export = Path.Combine(profile.Root, "Accounting PC");
        Directory.CreateDirectory(Path.Combine(export, "Virtual Machines"));
        Directory.CreateDirectory(Path.Combine(export, "Virtual Hard Disks"));
        var vmcx = Path.Combine(export, "Virtual Machines", "4B6F9E2A-1C3D-4E5F-8A9B-0C1D2E3F4A5B.vmcx");
        File.WriteAllBytes(vmcx, [0x01, 0x02]);
        File.Move(Disk(profile, "disk", WindowsGpt, "vhdx"), Path.Combine(export, "Virtual Hard Disks", "Accounting PC.vhdx"));
        var machine = MachineImporter.HyperV(vmcx);
        Assert.Empty(machine.Problems);
        Assert.Equal("Accounting PC", machine.Name);
        Assert.Equal(("windows", FirmwareKind.Uefi), (machine.Guest.Os, machine.Firmware));
        Assert.EndsWith("Accounting PC.vhdx", machine.Disks.Single().Path, StringComparison.Ordinal);
        Assert.Contains("only Hyper-V reads", machine.Notes[0], StringComparison.Ordinal);

        Directory.Delete(Path.Combine(export, "Virtual Hard Disks"), true);
        Assert.Contains("No disk was found", MachineImporter.HyperV(vmcx).Problems.Single(), StringComparison.Ordinal);
    }

    /// <summary>Control: the decision on bytes alone, so a broken reader cannot pass as "no table".</summary>
    [Fact]
    public void The_decision_needs_the_boot_signature_and_reads_mbr_types()
    {
        var bytes = new byte[1024];
        Assert.Null(DiskProbe.Decide(bytes).Table);
        bytes[510] = 0x55;
        bytes[511] = 0xAA;
        bytes[446 + 4] = 0x07;
        Assert.Equal(("mbr", "windows"), (DiskProbe.Decide(bytes).Table, DiskProbe.Decide(bytes).Os));
        Assert.Null(DiskProbe.ParseDump("read failed\n", 1024));
    }

    [Fact]
    public void Every_list_of_files_comes_from_one_place()
    {
        Assert.Contains(".vmx", ImportFormats.MachineExtensions);
        Assert.Contains(".vmcx", ImportFormats.MachineExtensions);
        Assert.Contains(".vhdx", ImportFormats.DiskExtensions);
        Assert.Equal(VmQontrol.Core.Media.DropKind.Import, VmQontrol.Core.Media.DroppedFile.Guess("/x/y.vmcx"));
        Assert.Equal(VmQontrol.Core.Media.DropKind.Import, VmQontrol.Core.Media.DroppedFile.Guess("/x/y.QED"));
        Assert.Contains("*.VBOX", ImportFormats.Patterns(ImportFormats.MachineExtensions));
    }
}
