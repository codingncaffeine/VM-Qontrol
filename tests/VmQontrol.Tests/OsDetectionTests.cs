using System.Text;
using VmQontrol.Core.Media;
using VmQontrol.Core.OsInfo;

namespace VmQontrol.Tests;

public sealed class OsDetectionTests
{
    /// <summary>A minimal ISO 9660 image: system area, primary descriptor, El Torito record, terminator.</summary>
    private static MemoryStream Iso(string volume, string publisher = "", bool bootable = true)
    {
        var image = new byte[2048 * 20];
        void Descriptor(int sector, byte type, Action<Span<byte>> fill)
        {
            var s = image.AsSpan(sector * 2048, 2048);
            s[0] = type;
            "CD001"u8.CopyTo(s[1..]);
            s[6] = 1;
            fill(s);
        }

        void Text(Span<byte> s, int offset, int length, string text)
        {
            s.Slice(offset, length).Fill((byte)' ');
            Encoding.ASCII.GetBytes(text).CopyTo(s[offset..]);
        }

        Descriptor(16, 1, s =>
        {
            Text(s, 40, 32, volume);
            Text(s, 318, 128, publisher);
            BitConverter.GetBytes(20u).CopyTo(s[80..]);
            BitConverter.GetBytes((ushort)2048).CopyTo(s[128..]);
        });
        if (bootable) Descriptor(17, 0, s => Encoding.ASCII.GetBytes("EL TORITO SPECIFICATION").CopyTo(s[7..]));
        Descriptor(bootable ? 18 : 17, 255, _ => { });
        return new MemoryStream(image);
    }

    [Fact]
    public void The_primary_descriptor_is_read()
    {
        var iso = IsoVolume.Read(Iso("CCCOMA_X64FRE_EN-US_DV9", "MICROSOFT CORPORATION"))!;
        Assert.Equal("CCCOMA_X64FRE_EN-US_DV9", iso.VolumeId);
        Assert.Equal("MICROSOFT CORPORATION", iso.PublisherId);
        Assert.True(iso.Bootable);
        Assert.Equal(20L * 2048, iso.VolumeBytes);
    }

    [Fact]
    public void An_html_page_saved_as_an_iso_is_not_a_disc() =>
        Assert.Null(IsoVolume.Read(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 40000)))));

    [Trait("Tier", "Integration")]
    [Fact]
    public void The_installed_database_names_Windows_and_flags_the_10_or_11_ambiguity()
    {
        var db = OsInfoDb.LoadInstalled();
        Assert.SkipWhen(db is null, "osinfo-db is not installed");
        Assert.True(db!.Entries.Count > 500);
        var match = db.Identify(IsoVolume.Read(Iso("CCCOMA_X64FRE_EN-US_DV9", "MICROSOFT CORPORATION"))!)!;
        var names = match.Ambiguous.Append(match.Os).Select(o => o.ShortId).ToList();
        Assert.Contains("win10", names);
        Assert.Contains("win11", names);
        Assert.Equal("windows", GuestMapping.ToGuest(match.Os).Os);
    }

    [Trait("Tier", "Integration")]
    [Fact]
    public void The_installed_database_names_an_Ubuntu_desktop_disc()
    {
        var db = OsInfoDb.LoadInstalled();
        Assert.SkipWhen(db is null, "osinfo-db is not installed");
        var match = db!.Identify(IsoVolume.Read(Iso("Ubuntu 24.04.1 LTS amd64"))!);
        Assert.NotNull(match);
        Assert.StartsWith("ubuntu", match!.Os.ShortId, StringComparison.Ordinal);
        Assert.Equal("linux", GuestMapping.ToGuest(match.Os).Os);
    }
}
