using System.Diagnostics;
using VmQontrol.Core.Import;
using VmQontrol.Core.Library;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>Phase 11: a machine exported as a folder or one .tar comes back in another library whole; a damaged copy is refused; the quickemu .conf reads back.</summary>
public sealed class ExportTests
{
    private static async Task<(MachineConfig M, string Dir)> Machine(TestProfile profile)
    {
        var dir = Path.Combine(profile.Paths.DefaultLibrary, "Traveller");
        Directory.CreateDirectory(Path.Combine(dir, "tpm"));
        await QemuImg.RunAsync(TestContext.Current.CancellationToken, "create", "-q", "-f", "qcow2", Path.Combine(dir, "disk0.qcow2"), "64M");
        Io(Path.Combine(dir, "disk0.qcow2"), "write -P 0x5a 0 1M");
        await QemuImg.RunAsync(TestContext.Current.CancellationToken, "snapshot", "-c", "before", Path.Combine(dir, "disk0.qcow2"));
        File.WriteAllBytes(Path.Combine(dir, "OVMF_VARS.fd"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(dir, "tpm", "tpm2-00.permall"), [4, 5]);
        File.WriteAllBytes(Path.Combine(dir, "unattended.iso"), [6]);
        var m = new MachineConfig
        {
            Name = "Traveller",
            Guest = new GuestConfig { Os = "windows", Release = "11" },
            MemoryMiB = 8192,
            Cpu = new CpuConfig { Cores = 4 },
            Tpm = true,
            Firmware = new FirmwareConfig { Kind = FirmwareKind.Uefi, SecureBoot = true },
            Disks = [new DiskConfig { File = "disk0.qcow2", SizeGiB = 1 }],
            Media = [new MediaConfig { Role = MediaRole.Installer, File = "/somewhere/else/win.iso", Connect = Connect.UntilInstalled },
                     new MediaConfig { Role = MediaRole.Unattended, File = "unattended.iso", Connect = Connect.UntilInstalled }],
            Nics = [new NicConfig { Mac = "52:54:00:aa:bb:cc" }],
        };
        MachineStore.Save(m, Path.Combine(dir, MachineStore.FileName));
        return (m, dir);
    }

    private static string Io(string path, string command)
    {
        var start = new ProcessStartInfo("qemu-io") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-f", "qcow2", "-c", command, path }) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return output;
    }

    private static readonly IProgress<ImportProgress> None = new Progress<ImportProgress>(_ => { });

    [Fact]
    public async Task A_folder_export_carries_disks_with_snapshots_firmware_and_TPM_and_lists_in_another_library()
    {
        using var profile = new TestProfile("export-folder");
        var (m, dir) = await Machine(profile);
        var elsewhere = Path.Combine(profile.Root, "usb-drive");
        Directory.CreateDirectory(elsewhere);
        var (folder, notes) = await MachineExport.ExportAsync(m, dir, elsewhere, asTar: false, None, TestContext.Current.CancellationToken);

        Assert.Empty(await MachineExport.VerifyAsync(folder, TestContext.Current.CancellationToken));
        Assert.Contains(notes, n => n.Contains("win.iso", StringComparison.Ordinal));
        var copy = MachineStore.Load(Path.Combine(folder, MachineStore.FileName));
        Assert.NotEqual(m.Id, copy.Id);
        Assert.Equal("52:54:00:aa:bb:cc", copy.Nics[0].Mac);
        Assert.Equal(["unattended.iso"], copy.Media.Select(x => x.File));
        Assert.True(File.Exists(Path.Combine(folder, "tpm", "tpm2-00.permall")));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(folder, "OVMF_VARS.fd")));
        Assert.Contains("read 1048576/1048576", Io(Path.Combine(folder, "disk0.qcow2"), "read -P 0x5a 0 1M"), StringComparison.Ordinal);
        Assert.Contains("before", await QemuImg.RunAsync(TestContext.Current.CancellationToken, "snapshot", "-l", Path.Combine(folder, "disk0.qcow2")), StringComparison.Ordinal);
        // Listed where it now is, beside nothing else.
        Assert.Contains(LibraryScanner.Scan(elsewhere), e => e.Machine?.Name == "Traveller");
    }

    [Fact]
    public async Task One_tar_unpacks_into_another_library_checked_and_a_damaged_one_is_refused()
    {
        using var profile = new TestProfile("export-tar");
        var (m, dir) = await Machine(profile);
        var (tar, _) = await MachineExport.ExportAsync(m, dir, profile.Root, asTar: true, None, TestContext.Current.CancellationToken);
        Assert.EndsWith("Traveller.tar", tar, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(profile.Root, "Traveller")));

        var other = Path.Combine(profile.Root, "other-library");
        Directory.CreateDirectory(other);
        var folder = await MachineExport.UnpackAsync(tar, other, TestContext.Current.CancellationToken);
        Assert.Equal(Path.Combine(other, "Traveller"), folder);
        Assert.Empty(await MachineExport.VerifyAsync(folder, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(Path.Combine(other, ".importing")) && Directory.EnumerateFileSystemEntries(Path.Combine(other, ".importing")).Any());

        // Damage a byte in the copy's firmware variables inside the .tar, and import again.
        var bytes = File.ReadAllBytes(tar);
        var at = IndexOf(bytes, [1, 2, 3]);
        bytes[at] = 9;
        File.WriteAllBytes(tar, bytes);
        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => MachineExport.UnpackAsync(tar, other, TestContext.Current.CancellationToken));
        Assert.Contains("OVMF_VARS.fd is damaged", ex.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(other, "Traveller (2)")));
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        return -1;
    }

    [Fact]
    public async Task The_quickemu_conf_reads_back_as_the_same_machine()
    {
        using var profile = new TestProfile("export-quickemu");
        var (m, dir) = await Machine(profile);
        var conf = MachineExport.WriteQuickemuConf(m, dir);
        Assert.Equal(Path.Combine(profile.Paths.DefaultLibrary, "Traveller.conf"), conf);
        var back = QuickemuImporter.Import(conf).Machine;
        Assert.Equal(Path.Combine(dir, "disk0.qcow2"), back.Disks[0].File);
        Assert.Equal((m.MemoryMiB, m.Cpu.Cores, m.Guest.Os, FirmwareKind.Uefi, true, true), (back.MemoryMiB, back.Cpu.Cores, back.Guest.Os, back.Firmware.Kind, back.Firmware.SecureBoot, back.Tpm));
        Assert.Equal("52:54:00:aa:bb:cc", back.Nics[0].Mac);
        // The library lists the machine once (from its vm.json), not again from the .conf beside it.
        Assert.Single(LibraryScanner.Scan(profile.Paths.DefaultLibrary), e => e.Machine?.Name == "Traveller");
    }
}
