using System.Diagnostics;
using System.Text;
using VmQontrol.Core.Media;

namespace VmQontrol.Tests;

public sealed class IsoWriterTests
{
    [Fact]
    public void A_written_disc_reads_back_with_its_label()
    {
        var stream = new MemoryStream();
        IsoWriter.Write(stream, "cidata", new Dictionary<string, byte[]> { ["user-data"] = "#cloud-config\n"u8.ToArray(), ["meta-data"] = "instance-id: x\n"u8.ToArray() });
        stream.Position = 0;
        var iso = IsoVolume.Read(stream)!;
        Assert.Equal("CIDATA", iso.VolumeId);
        Assert.Equal(0, stream.Length % 2048);
    }

    /// <summary>The control: the system's own ISO reader (isoinfo) lists the Joliet names and reads the files back.</summary>
    [Trait("Tier", "Integration")]
    [Fact]
    public void The_system_iso_reader_sees_the_real_file_names_and_contents()
    {
        Assert.SkipWhen(!File.Exists("/usr/bin/isoinfo"), "isoinfo is not installed");
        // Kept in scratch/iso (overwritten each run) so a failure can be examined with the system's own tools.
        var dir = Path.Combine(TestProfile.ScratchRoot, "iso");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "seed.iso");
        var userData = "#cloud-config\npackages: [qemu-guest-agent]\n" + new string('x', 5000);
        IsoWriter.Write(file, "cidata", new Dictionary<string, byte[]> { ["user-data"] = Encoding.UTF8.GetBytes(userData), ["meta-data"] = "instance-id: vmq\n"u8.ToArray(), ["autounattend.xml"] = "<unattend/>"u8.ToArray() });
        var list = Run("isoinfo", "-J", "-f", "-i", file);
        Assert.Contains("/user-data", list, StringComparison.Ordinal);
        Assert.Contains("/meta-data", list, StringComparison.Ordinal);
        Assert.Contains("/autounattend.xml", list, StringComparison.Ordinal);
        Assert.Equal(userData, Run("isoinfo", "-J", "-x", "/user-data", "-i", file));
    }

    private static string Run(string program, params string[] args)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }
}
