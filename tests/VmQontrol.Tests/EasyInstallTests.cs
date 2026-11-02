using System.Xml;
using VmQontrol.Core.Install;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Media;

namespace VmQontrol.Tests;

public sealed class EasyInstallTests
{
    private static XmlDocument Parse(string xml)
    {
        var doc = new XmlDocument();
        doc.LoadXml(xml);
        return doc;
    }

    private static XmlNamespaceManager Ns(XmlDocument doc)
    {
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("u", "urn:schemas-microsoft-com:unattend");
        return ns;
    }

    [Fact]
    public void A_UEFI_answer_file_makes_EFI_MSR_and_Windows_partitions_and_installs_the_chosen_edition()
    {
        var doc = Parse(WindowsAnswerFile.Build(new WindowsInstallChoices { UserName = "Ann", Password = "p<&>\"'", ComputerName = "LAB", EditionIndex = 6 }, "11"));
        var ns = Ns(doc);
        Assert.Equal(["EFI", "MSR", "Primary"], doc.SelectNodes("//u:CreatePartition/u:Type", ns)!.Cast<XmlNode>().Select(n => n.InnerText));
        Assert.Equal("6", doc.SelectSingleNode("//u:InstallFrom/u:MetaData/u:Value", ns)!.InnerText);
        Assert.Equal("3", doc.SelectSingleNode("//u:InstallTo/u:PartitionID", ns)!.InnerText);
        // The password survives XML escaping exactly.
        Assert.Equal("p<&>\"'", doc.SelectSingleNode("//u:LocalAccount/u:Password/u:Value", ns)!.InnerText);
        Assert.Contains(doc.SelectNodes("//u:PathAndCredentials/u:Path", ns)!.Cast<XmlNode>(), n => n.InnerText == @"E:\viostor\w11\amd64");
        Assert.Contains("virtio-win-guest-tools.exe", doc.SelectSingleNode("//u:FirstLogonCommands", ns)!.InnerText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_BIOS_answer_file_without_drivers_has_one_active_partition_and_no_driver_paths()
    {
        var doc = Parse(WindowsAnswerFile.Build(new WindowsInstallChoices { UserName = "Ann", ComputerName = "LAB", Uefi = false, VirtioDrivers = false }, "10"));
        var ns = Ns(doc);
        Assert.Equal(["Primary"], doc.SelectNodes("//u:CreatePartition/u:Type", ns)!.Cast<XmlNode>().Select(n => n.InnerText));
        Assert.Equal("true", doc.SelectSingleNode("//u:ModifyPartition/u:Active", ns)!.InnerText);
        Assert.Null(doc.SelectSingleNode("//u:PathAndCredentials", ns));
    }

    [Fact]
    public void Windows_11_checks_are_bypassed_only_when_asked()
    {
        Assert.DoesNotContain("LabConfig", WindowsAnswerFile.Build(new WindowsInstallChoices { UserName = "a", ComputerName = "b" }, "11"), StringComparison.Ordinal);
        Assert.Contains("BypassTPMCheck", WindowsAnswerFile.Build(new WindowsInstallChoices { UserName = "a", ComputerName = "b", BypassWindows11Checks = true }, "11"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("My Lab VM", "MYLABVM")]
    [InlineData("windows-11-development-box", "WINDOWS-11-DEVE")]
    [InlineData("1234", "VM-1234")]
    [InlineData("!!", "VM-")]
    public void Computer_names_follow_Windows_rules(string machine, string expected) => Assert.Equal(expected, WindowsAnswerFile.ComputerName(machine));

    [Fact]
    public void The_factory_writes_an_answer_disc_and_falls_back_to_SATA_without_drivers()
    {
        using var profile = new TestProfile("factory");
        var (m, dir) = MachineFactory.Create(new NewMachineRequest
        {
            Name = "Win",
            LibraryRoot = profile.Paths.DefaultLibrary,
            Guest = new GuestConfig { Os = "windows", Release = "10" },
            InstallerIso = "/isos/win10.iso",
            WindowsEasyInstall = new WindowsInstallChoices { UserName = "Ann", ComputerName = "WIN" },
        });
        Assert.True(File.Exists(Path.Combine(dir, "unattended.iso")));
        Assert.Equal("UNATTEND", IsoVolume.Read(Path.Combine(dir, "unattended.iso"))!.VolumeId);
        Assert.Equal(DiskBus.Sata, m.Disks[0].Bus);
        Assert.Equal("e1000", m.Nics[0].Model);
        Assert.Contains(m.Media, x => x.Role == MediaRole.Unattended && x.Connect == Connect.UntilInstalled);
        // A second machine with the same name gets its own folder.
        var (_, second) = MachineFactory.Create(new NewMachineRequest { Name = "Win", LibraryRoot = profile.Paths.DefaultLibrary, Guest = new GuestConfig() });
        Assert.EndsWith("Win (2)", second, StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_11_is_recommended_its_minimums_even_on_a_small_host()
    {
        var host = RecordedHost.Load() with { MemoryGB = 6, LogicalCpus = 2 };
        var r = Recommendations.For(new GuestConfig { Os = "windows", Release = "11" }, null, host);
        Assert.Equal(4096, r.MemoryMiB.Value);
        Assert.Equal(2, r.Cores.Value);
        Assert.True(r.DiskGiB.Value >= 64);
    }

    [Fact]
    public void A_large_host_gives_a_Linux_guest_a_comfortable_share()
    {
        var r = Recommendations.For(new GuestConfig { Os = "linux" }, null, RecordedHost.Load());
        Assert.Equal(16384, r.MemoryMiB.Value);
        Assert.Equal(16, r.Cores.Value);
        Assert.Contains("64 GB", r.MemoryMiB.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Windows_image_catalogue_lists_editions_with_their_build()
    {
        var editions = WindowsMedia.ParseCatalogue("""
            <WIM><IMAGE INDEX="1"><NAME>Windows 11 Home</NAME><DISPLAYNAME>Windows 11 Home</DISPLAYNAME><WINDOWS><VERSION><BUILD>26100</BUILD></VERSION></WINDOWS></IMAGE>
            <IMAGE INDEX="6"><NAME>Windows 11 Pro</NAME><WINDOWS><VERSION><BUILD>26100</BUILD></VERSION></WINDOWS></IMAGE></WIM>
            """);
        Assert.Equal([(1, "Windows 11 Home", 26100), (6, "Windows 11 Pro", 26100)], editions.Select(e => (e.Index, e.DisplayName, e.Build)));
    }

    [Theory]
    [InlineData("vb_release", 19041)]
    [InlineData("ge_release_svc_prod1", 26100)]
    [InlineData("ni_release", 22621)]
    public void A_build_branch_names_its_first_build(string branch, int build) => Assert.Equal(build, WindowsMedia.BranchBuild(branch));

    /// <summary>The disc in the dev folder (never committed): Windows 10 22H2, which libosinfo names "Windows 11".</summary>
    [Trait("Tier", "Integration")]
    [Fact]
    public void The_local_Windows_10_disc_is_named_exactly()
    {
        var iso = Path.Combine(Path.GetDirectoryName(TestProfile.ScratchRoot)!, "Win10_22H2_English_x64v1.iso");
        Assert.SkipWhen(!File.Exists(iso), "The Windows 10 disc is not in the dev folder.");
        var info = WindowsMedia.Inspect(iso)!;
        Assert.Equal("10", info.Release);
        Assert.False(info.Server);
        Assert.Contains(info.Editions, e => e.DisplayName == "Windows 10 Pro");
    }

    [Theory]
    [InlineData("Windows 11 Pro", "W269N-WFGWX-YVC9B-37TCY-YTCJR")]
    [InlineData("Windows 10 Pro N", "MH37W-N47XK-V7XM9-C7227-GCQG9")]
    [InlineData("Windows 10 Pro N for Workstations", "9FNHH-K3HBT-3W4TD-6383H-6XYWF")]
    [InlineData("Windows 11 Education", "NW6C2-QMPVW-D7KKK-3GKT6-VCFB2")]
    [InlineData("Windows 10 Home", null)]
    [InlineData("Windows 11 Home Single Language", null)]
    public void Each_edition_gets_its_published_setup_key(string edition, string? key) => Assert.Equal(key, WindowsSetupKeys.For(edition));

    [Fact]
    public void The_answer_file_uses_the_setup_key_unless_the_person_gave_their_own()
    {
        var pro = new WindowsInstallChoices { UserName = "a", ComputerName = "b", EditionName = "Windows 10 Pro" };
        Assert.Contains("W269N-WFGWX-YVC9B-37TCY-YTCJR", WindowsAnswerFile.Build(pro, "10"), StringComparison.Ordinal);
        Assert.Contains("AAAAA-BBBBB-CCCCC-DDDDD-EEEEE", WindowsAnswerFile.Build(pro with { ProductKey = "AAAAA-BBBBB-CCCCC-DDDDD-EEEEE" }, "10"), StringComparison.Ordinal);
        Assert.False(WindowsAnswerFile.HasKey(pro with { EditionName = "Windows 10 Home" }));
    }
}
