using VmQontrol.Cli;

namespace VmQontrol.Tests;

public sealed class CliTests
{
    private static (int Code, string Out, string Err) Vmq(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = Commands.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void Version_names_the_tool_and_the_license()
    {
        var (code, output, _) = Vmq("version");
        Assert.Equal(0, code);
        Assert.StartsWith("vmq ", output, StringComparison.Ordinal);
        Assert.Contains("GPLv3", output, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_command_fails_with_a_pointer_to_help()
    {
        var (code, _, error) = Vmq("frobnicate");
        Assert.Equal(2, code);
        Assert.Contains("vmq help", error, StringComparison.Ordinal);
    }
}

public sealed class CliMachineTests
{
    private static (int Code, string Out, string Err) Vmq(TestProfile profile, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = Commands.Run(args, output, error, profile.Paths, RecordedHost.Load);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void List_shows_quickemu_machines_and_their_problems()
    {
        using var profile = new TestProfile("cli");
        var lib = profile.Paths.DefaultLibrary;
        Directory.CreateDirectory(Path.Combine(lib, "linux"));
        File.Copy(Path.Combine(RecordedHost.FixtureDirectory, "linux.conf"), Path.Combine(lib, "linux.conf"));
        var (code, output, _) = Vmq(profile, "list");
        Assert.Equal(0, code);
        Assert.Contains("linux", output, StringComparison.Ordinal);
        Assert.Contains("quickemu", output, StringComparison.Ordinal);
        Assert.Contains("problem", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_names_a_missing_installer_and_exits_3()
    {
        using var profile = new TestProfile("cli");
        var lib = profile.Paths.DefaultLibrary;
        Directory.CreateDirectory(lib);
        var conf = Path.Combine(lib, "linux.conf");
        File.Copy(Path.Combine(RecordedHost.FixtureDirectory, "linux.conf"), conf);
        var (code, output, _) = Vmq(profile, "inspect", conf);
        Assert.Equal(3, code);
        Assert.Contains("installer disc", output, StringComparison.Ordinal);
        Assert.Contains("-machine q35", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Detect_refuses_a_file_that_is_not_a_disc_image()
    {
        using var profile = new TestProfile("cli");
        var fake = Path.Combine(profile.Root, "virtio-win.iso");
        File.WriteAllText(fake, "<!doctype html><title>Making sure you're not a bot!</title>");
        var (code, _, error) = Vmq(profile, "detect", fake);
        Assert.Equal(1, code);
        Assert.Contains("not a disc image", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_quickemu_machine_keeps_the_same_id_across_reads()
    {
        var conf = Path.Combine(RecordedHost.FixtureDirectory, "linux.conf");
        Assert.Equal(VmQontrol.Core.Import.QuickemuImporter.Import(conf).Machine.Id, VmQontrol.Core.Import.QuickemuImporter.Import(conf).Machine.Id);
    }
}
