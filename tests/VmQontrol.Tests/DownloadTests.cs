using VmQontrol.Core.Downloads;
using VmQontrol.Core.Import;
using VmQontrol.Core.Machines;

namespace VmQontrol.Tests;

public sealed class DownloadTests
{
    [Fact]
    public void The_catalogue_groups_systems_and_orders_releases_newest_first()
    {
        var systems = Catalogue.Parse("""
            [{"Display Name":"Ubuntu","OS":"ubuntu","Release":"9.10","Option":"","PNG":"u.png"},
             {"Display Name":"Ubuntu","OS":"ubuntu","Release":"24.04","Option":"","PNG":"u.png"},
             {"Display Name":"Ubuntu","OS":"ubuntu","Release":"daily-live","Option":"","PNG":"u.png"},
             {"Display Name":"Alpine Linux","OS":"alpine","Release":"v3.22","Option":"","PNG":"a.png"}]
            """);
        Assert.Equal(["Alpine Linux", "Ubuntu"], systems.Select(s => s.DisplayName));
        Assert.Equal(["24.04", "9.10", "daily-live"], systems[1].Entries.Select(e => e.Release));
    }

    [Fact]
    public void A_downloaded_quickemu_machine_is_adopted_with_paths_inside_its_own_folder()
    {
        using var profile = new TestProfile("adopt");
        var staging = Path.Combine(profile.Root, "staging");
        Directory.CreateDirectory(Path.Combine(staging, "ubuntu-24.04"));
        File.WriteAllText(Path.Combine(staging, "ubuntu-24.04", "ubuntu-24.04.iso"), "x");
        File.WriteAllText(Path.Combine(staging, "ubuntu-24.04.conf"), "guest_os=\"linux\"\ndisk_img=\"ubuntu-24.04/disk.qcow2\"\niso=\"ubuntu-24.04/ubuntu-24.04.iso\"\n");
        var import = QuickemuImporter.Import(Path.Combine(staging, "ubuntu-24.04.conf"));
        var (m, dir) = DownloadJob.Adopt(import, profile.Paths.DefaultLibrary, "Ubuntu 24.04");
        Assert.Equal(Path.Combine(profile.Paths.DefaultLibrary, "Ubuntu 24.04"), dir);
        Assert.Equal("ubuntu-24.04.iso", m.Media.Single(x => x.Role == MediaRole.Installer).File);
        Assert.Equal("disk.qcow2", m.Disks[0].File);
        Assert.Null(m.ImportedFrom);
        Assert.True(File.Exists(Path.Combine(dir, "vm.json")));
        Assert.False(Directory.Exists(Path.Combine(staging, "ubuntu-24.04")));
    }

    /// <summary>A real download through quickget (TinyCore, about 17 MB): verified, adopted, and its staging folder gone.</summary>
    [Trait("Tier", "Guest")]
    [Fact]
    public async Task A_real_download_becomes_a_ready_machine()
    {
        Assert.SkipWhen(!File.Exists("/usr/bin/quickget"), "quickget is not installed");
        using var profile = new TestProfile("download");
        Directory.CreateDirectory(profile.Paths.DefaultLibrary);
        var reports = new List<DownloadProgress>();
        var job = new DownloadJob(new CatalogueEntry("Tiny Core Linux", "tinycore", "15", "Core", ""), profile.Paths.DefaultLibrary, "Tiny Core");
        var (m, dir) = await job.RunAsync(new Progress<DownloadProgress>(reports.Add), TestContext.Current.CancellationToken);
        Assert.Empty(MachineChecks.Problems(m, dir));
        Assert.NotNull(VmQontrol.Core.Media.IsoVolume.Read(Path.Combine(dir, m.Media.Single(x => x.Role == MediaRole.Installer).File)));
        Assert.False(Directory.Exists(Path.Combine(profile.Paths.DefaultLibrary, ".downloads")));
        Assert.Contains(reports, r => r.ExpectedBytes is > 10_000_000);
    }

    [Fact]
    public void A_short_catalogue_entry_gets_a_staging_folder_too()
    {
        // macOS, Alpine and every entry without an edition made a name under 48 characters, and cutting it threw.
        var shortName = DownloadJob.StagingName(new CatalogueEntry("macOS", "macos", "sonoma", "", ""));
        Assert.StartsWith("macos-sonoma-", shortName, StringComparison.Ordinal);
        Assert.NotEqual(shortName, DownloadJob.StagingName(new CatalogueEntry("macOS", "macos", "sonoma", "", "")));
        var longName = DownloadJob.StagingName(new CatalogueEntry("Windows", "windows", "11", "English International", ""));
        Assert.True(longName.Length <= 48, longName);
        Assert.False(longName.EndsWith('-'));
    }
}
