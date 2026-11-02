using System.Diagnostics;
using VmQontrol.Core.Install;
using VmQontrol.Core.Media;

namespace VmQontrol.Tests;

public sealed class LinuxInstallToolsTests
{
    /// <summary>The specification's own test vector.</summary>
    [Fact]
    public void Sha512_crypt_matches_the_published_test_vector() =>
        Assert.Equal("$6$saltstring$svn8UoSVapNtMuq1ukKS4tPQd8iKwSMHWjl/O817G3uBnIFNjnQJuesI68u4OTLiBFdcbYEdFCoEOfaS35inz1",
            Sha512Crypt.Hash("Hello world!", "saltstring"));

    /// <summary>The control: OpenSSL's own implementation agrees on a password with UTF-8 and a long length.</summary>
    [Trait("Tier", "Integration")]
    [Fact]
    public void Sha512_crypt_matches_openssl()
    {
        Assert.SkipWhen(!File.Exists("/usr/bin/openssl"), "openssl is not installed");
        const string password = "Qontrol-1 ünïcode and a password longer than sixty-four bytes in total to cover the loop";
        var start = new ProcessStartInfo("openssl") { RedirectStandardOutput = true, ArgumentList = { "passwd", "-6", "-salt", "AbCdEfGh12345678", password } };
        using var p = Process.Start(start)!;
        var expected = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        Assert.Equal(expected, Sha512Crypt.Hash(password, "AbCdEfGh12345678"));
    }

    [Fact]
    public void Iso_files_are_read_by_their_real_names_through_joliet()
    {
        var stream = new MemoryStream();
        IsoWriter.Write(stream, "Test", new Dictionary<string, byte[]> { ["vmlinuz"] = [1, 2, 3], ["initrd.gz"] = new byte[5000] });
        stream.Position = 0;
        using var iso = IsoFiles.Open(stream)!;
        Assert.Equal(new byte[] { 1, 2, 3 }, iso.Read("vmlinuz"));
        Assert.Equal(5000, iso.Read("INITRD.GZ")!.Length);
        Assert.Null(iso.Read("missing"));
    }

    /// <summary>The control: the system's cpio reads the archive back with the same names and bytes.</summary>
    [Trait("Tier", "Integration")]
    [Fact]
    public void A_cpio_archive_reads_back_with_the_system_cpio()
    {
        Assert.SkipWhen(!File.Exists("/usr/bin/cpio") && !File.Exists("/usr/bin/bsdcpio"), "cpio is not installed");
        using var profile = new TestProfile("cpio");
        var archive = Path.Combine(profile.Root, "add.cpio");
        File.WriteAllBytes(archive, Cpio.Newc(new Dictionary<string, byte[]> { ["preseed.cfg"] = "d-i debconf/priority string critical\n"u8.ToArray() }));
        var start = new ProcessStartInfo(File.Exists("/usr/bin/cpio") ? "cpio" : "bsdcpio") { WorkingDirectory = profile.Root, RedirectStandardInput = true, RedirectStandardError = true, ArgumentList = { "-id" } };
        using (var p = Process.Start(start)!)
        {
            p.StandardInput.BaseStream.Write(File.ReadAllBytes(archive));
            p.StandardInput.Close();
            p.WaitForExit();
        }

        Assert.Equal("d-i debconf/priority string critical\n", File.ReadAllText(Path.Combine(profile.Root, "preseed.cfg")));
    }
}
