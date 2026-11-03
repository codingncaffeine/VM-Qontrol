using VmQontrol.Cli;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>vmq doctor and vmq import, the command-line faces of the host check and of Import a machine.</summary>
public sealed class CliImportDoctorTests
{
    [Fact]
    public void Doctor_lists_every_check_and_says_how_many_are_missing()
    {
        using var profile = new TestProfile("cli-doctor");
        var output = new StringWriter();
        var rc = Commands.Run(["doctor"], output, new StringWriter(), profile.Paths, () => RecordedHost.Load());
        var text = output.ToString();
        Assert.Contains("QEMU", text, StringComparison.Ordinal);
        Assert.Contains("TPM emulator", text, StringComparison.Ordinal);
        Assert.Matches(rc == 0 ? "Ready \\(" : "\\d+ missing \\(", text);
    }

    [Fact]
    public async Task Import_copies_a_VMware_machine_into_the_library_and_names_its_notes()
    {
        using var profile = new TestProfile("cli-import");
        var src = Path.Combine(profile.Root, "box");
        Directory.CreateDirectory(src);
        await QemuImg.RunAsync(TestContext.Current.CancellationToken, "create", "-q", "-f", "vmdk", Path.Combine(src, "box.vmdk"), "32M");
        File.WriteAllText(Path.Combine(src, "box.vmx"), """
            displayName = "Box"
            guestOS = "debian12-64"
            sata0.present = "TRUE"
            sata0:0.present = "TRUE"
            sata0:0.fileName = "box.vmdk"
            ethernet0.present = "TRUE"
            ethernet0.connectionType = "bridged"
            """);
        var output = new StringWriter();
        Assert.Equal(0, Commands.Run(["import", Path.Combine(src, "box.vmx"), "--name", "Imported box"], output, new StringWriter(), profile.Paths, () => RecordedHost.Load()));
        Assert.Contains("note: Network adapter 1 was bridged", output.ToString(), StringComparison.Ordinal);
        var saved = MachineStore.Load(Path.Combine(profile.Paths.DefaultLibrary, "Imported box", MachineStore.FileName));
        Assert.Equal(("Imported box", DiskBus.Sata), (saved.Name, saved.Disks[0].Bus));
    }
}
