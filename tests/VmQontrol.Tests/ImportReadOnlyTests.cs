using System.Security.Cryptography;
using VmQontrol.Core.Import;

namespace VmQontrol.Tests;

/// <summary>Importing a quickemu machine only reads: every file in its folder is byte-identical afterwards, none added.</summary>
public sealed class ImportReadOnlyTests
{
    [Fact]
    public void Import_leaves_the_quickemu_folder_exactly_as_it_was()
    {
        using var profile = new TestProfile("import");
        var root = Path.Combine(profile.Root, "VMs");
        Directory.CreateDirectory(Path.Combine(root, "windows-11", "unattended"));
        File.WriteAllText(Path.Combine(root, "windows-11.conf"), File.ReadAllText(Path.Combine(RecordedHost.FixtureDirectory, "windows-11.conf")));
        File.WriteAllText(Path.Combine(root, "windows-11", "virtio-win.iso"), "x");
        File.WriteAllText(Path.Combine(root, "windows-11", "unattended", "autounattend.xml"), "<x/>");
        var before = Snapshot(root);

        var import = QuickemuImporter.Import(Path.Combine(root, "windows-11.conf"));

        Assert.Equal(before, Snapshot(root));
        Assert.Equal("windows", import.Machine.Guest.Os);
        Assert.Equal("11", import.Machine.Guest.Release);
        Assert.True(import.Machine.Tpm);
        Assert.Equal(Path.Combine(root, "windows-11", "disk.qcow2"), import.Machine.Disks[0].File);
    }

    private static string Snapshot(string root) => string.Join('\n', Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(p => File.Exists(p) ? $"{p} {Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))} {File.GetLastWriteTimeUtc(p):O}" : p));
}
