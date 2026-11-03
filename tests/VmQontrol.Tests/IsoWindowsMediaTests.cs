using VmQontrol.Core.Media;

namespace VmQontrol.Tests;

/// <summary>Windows on an ISO 9660 + Joliet disc (no UDF): the discs VM Qontrol builds from Microsoft's download.</summary>
public sealed class IsoWindowsMediaTests
{
    /// <summary>A real built disc, when one is at hand: VMQONTROL_BUILT_WINDOWS_ISO=path (its install.esd is over 4 GiB, so multi-extent).</summary>
    [Fact]
    public void A_built_disc_names_its_windows_and_editions()
    {
        if (Environment.GetEnvironmentVariable("VMQONTROL_BUILT_WINDOWS_ISO") is not { Length: > 0 } iso) return;
        using (var files = IsoFiles.Open(iso)!)
        {
            Assert.NotNull(files.Read("sources/idwbinfo.txt", 65536));
            Assert.True(files.SizeOf("sources/install.esd") > 4L * 1024 * 1024 * 1024, $"size {files.SizeOf("sources/install.esd")}");
            Assert.Equal("MSWIM"u8.ToArray(), files.ReadAt("sources/install.esd", 0, 5));
        }

        var w = WindowsMedia.Inspect(iso);
        Assert.NotNull(w);
        Assert.Equal("11", w.Release);
        Assert.Contains(w.Editions, e => e.Name == "Windows 11 Pro" && e.EditionId == "Professional");
    }
}
