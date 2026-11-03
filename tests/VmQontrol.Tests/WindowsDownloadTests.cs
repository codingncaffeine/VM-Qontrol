using System.Buffers.Binary;
using System.Text;
using VmQontrol.Core.Downloads;
using VmQontrol.Core.Install;
using VmQontrol.Core.Media;

namespace VmQontrol.Tests;

/// <summary>Windows from Microsoft's Media Creation Tool catalog, and the disc it becomes.</summary>
public sealed class WindowsDownloadTests
{
    private static string FileXml(string lang, string arch, string edition, string name, string sha1 = "AB12", string host = "dl.delivery.mp.microsoft.com") => $"""
        <File id="{name}_{edition}"><FileName>{name}</FileName><LanguageCode>{lang}</LanguageCode><Language>x</Language><Edition>{edition}</Edition>
        <Architecture>{arch}</Architecture><Size>4680320173</Size><Sha1>{sha1}</Sha1><FilePath>http://{host}/filestreamingservice/files/x/{name}</FilePath>
        <Key /><Architecture_Loc>%ARCH_64%</Architecture_Loc><Edition_Loc>%BASE_CHINA%</Edition_Loc><IsRetailOnly>False</IsRetailOnly></File>
        """;

    /// <summary>A trimmed copy of products.xml as the Windows 11 cabinet holds it (same element names and nesting).</summary>
    internal static string Catalog()
    {
        const string consumer = "26100.4349.250607-1500.ge_release_svc_refresh_CLIENTCONSUMER_RET_x64FRE_en-us.esd";
        const string business = "26100.4349.250607-1500.ge_release_svc_refresh_CLIENTBUSINESS_VOL_x64FRE_en-us.esd";
        const string german = "26100.4349.250607-1500.ge_release_svc_refresh_CLIENTCONSUMER_RET_x64FRE_de-de.esd";
        var files = string.Concat(
            FileXml("en-us", "x64", "Professional", consumer, "8CEAB2838F8E90180AC7490E8752157FB05588CB"),
            FileXml("en-us", "x64", "Core", consumer, "8CEAB2838F8E90180AC7490E8752157FB05588CB"),
            FileXml("en-us", "x64", "Ultimate", consumer, "8CEAB2838F8E90180AC7490E8752157FB05588CB"),
            FileXml("en-us", "x64", "Enterprise", business),
            FileXml("en-gb", "x64", "Professional", consumer.Replace("en-us", "en-gb")),
            FileXml("de-de", "x64", "Professional", german),
            FileXml("en-us", "arm64", "Professional", consumer.Replace("x64", "A64")),
            FileXml("en-us", "x64", "Professional", "elsewhere.esd", host: "example.com"));
        return $"""<?xml version="1.0" encoding="UTF-8"?><MCT><Catalogs><Catalog version="1.0"><PublishedMedia><Files>{files}</Files></PublishedMedia></Catalog></Catalogs></MCT>""";
    }

    [Fact]
    public void The_catalog_gives_one_file_per_language_and_architecture_with_its_editions()
    {
        var files = WindowsDownload.Parse(Catalog(), "11");
        Assert.DoesNotContain(files, f => f.FileName == "elsewhere.esd"); // only Microsoft's own download network
        var consumer = Assert.Single(files, f => f.Language == "en-us" && f.Architecture == "x64" && !f.Business);
        Assert.Equal(["Professional", "Core", "Ultimate"], consumer.EditionIds);
        Assert.Equal("8ceab2838f8e90180ac7490e8752157fb05588cb", consumer.Sha1);
        Assert.Equal("26100.4349", consumer.Build);
        Assert.Equal(4680320173, consumer.Size);
        Assert.Single(files, f => f.Language == "en-us" && f.Business);
    }

    [Fact]
    public void Editions_follow_the_language_and_leave_out_names_the_file_does_not_hold()
    {
        var files = WindowsDownload.Parse(Catalog(), "11");
        var us = WindowsDownload.Editions(files, "en-US");
        Assert.Equal(["Windows 11 Pro", "Windows 11 Home", "Windows 11 Enterprise"], us.Select(e => e.Name));
        Assert.True(us.Single(e => e.EditionId == "Enterprise").File.Business);
        Assert.Equal("de-de", WindowsDownload.Editions(files, "de-DE")[0].File.Language);
        Assert.Equal("en-gb", WindowsDownload.Editions(files, "en-GB")[0].File.Language);
        // No file in the region's own language: the same language elsewhere, else English (United States).
        Assert.Equal("de-de", WindowsDownload.Editions(files, "de-AT")[0].File.Language);
        Assert.Equal("en-us", WindowsDownload.Editions(files, "ja-JP")[0].File.Language);
        Assert.Equal("arm64", WindowsDownload.Editions(files, "en-US", "arm64")[0].File.Architecture);
    }

    [Fact]
    public void A_downloaded_discs_language_drives_setup_while_the_region_stays_the_persons()
    {
        var xml = WindowsAnswerFile.Build(new WindowsInstallChoices { UserName = "A", ComputerName = "PC", Locale = "de-AT", UiLanguage = "de-DE", EditionName = "Windows 11 Pro", ConsumerDisc = true }, "11");
        Assert.Contains("<UILanguage>de-DE</UILanguage>", xml);
        Assert.DoesNotContain("<UILanguage>de-AT</UILanguage>", xml);
        Assert.Contains("<UserLocale>de-AT</UserLocale>", xml);
        Assert.Contains("<SystemLocale>de-AT</SystemLocale>", xml);
        // A disc the person picked: one language for all, as before.
        var own = WindowsAnswerFile.Build(new WindowsInstallChoices { UserName = "A", ComputerName = "PC", Locale = "en-GB", EditionName = "Windows 11 Pro", ConsumerDisc = true }, "11");
        Assert.Contains("<UILanguage>en-GB</UILanguage>", own);
    }

    [Fact]
    public void Missing_tools_are_named_before_anything_is_downloaded()
    {
        Assert.Equal(["wimlib-imagex", "xorriso", "bsdtar"], WindowsDownload.MissingTools(_ => false));
        Assert.Empty(WindowsDownload.MissingTools(_ => true));
        Assert.Empty(WindowsDownload.MissingTools(p => p != "bsdtar")); // cabextract does as well
    }

    /// <summary>
    /// A file stored in pieces (ISO 9660 level 3 "multi-extent", how a disc holds a file over 4 GiB): directory
    /// records of one name, each but the last flagged "not final", read as one file. Built by hand, a few sectors.
    /// </summary>
    [Fact]
    public void A_file_in_several_pieces_reads_as_one()
    {
        const int s = 2048;
        var iso = new byte[30 * s];
        static void Record(byte[] disc, ref int at, string name, uint extent, uint length, byte flags)
        {
            var n = Encoding.ASCII.GetBytes(name);
            var len = 33 + n.Length + (n.Length % 2 == 0 ? 1 : 0);
            disc[at] = (byte)len;
            BinaryPrimitives.WriteUInt32LittleEndian(disc.AsSpan(at + 2), extent);
            BinaryPrimitives.WriteUInt32BigEndian(disc.AsSpan(at + 6), extent);
            BinaryPrimitives.WriteUInt32LittleEndian(disc.AsSpan(at + 10), length);
            BinaryPrimitives.WriteUInt32BigEndian(disc.AsSpan(at + 14), length);
            disc[at + 25] = flags;
            disc[at + 32] = (byte)n.Length;
            n.CopyTo(disc, at + 33);
            at += len;
        }

        // Primary volume descriptor (sector 16) whose root directory is at sector 20; the set terminator at 17.
        iso[16 * s] = 1;
        "CD001"u8.CopyTo(iso.AsSpan(16 * s + 1));
        var root = 16 * s + 156;
        Record(iso, ref root, "\0", 20, s, 2);
        iso[17 * s] = 255;
        "CD001"u8.CopyTo(iso.AsSpan(17 * s + 1));
        var at = 20 * s;
        Record(iso, ref at, "\0", 20, s, 2);
        Record(iso, ref at, "\u0001", 20, s, 2);
        Record(iso, ref at, "INSTALL.ESD;1", 22, 2 * s, 0x80);
        Record(iso, ref at, "INSTALL.ESD;1", 25, 2 * s, 0x80);
        Record(iso, ref at, "INSTALL.ESD;1", 28, 100, 0);
        Record(iso, ref at, "OTHER.TXT;1", 24, 5, 0);
        for (var i = 0; i < 2 * s; i++) { iso[22 * s + i] = (byte)'a'; iso[25 * s + i] = (byte)'b'; }
        for (var i = 0; i < 100; i++) iso[28 * s + i] = (byte)'c';
        "hello"u8.CopyTo(iso.AsSpan(24 * s));

        using var files = IsoFiles.Open(new MemoryStream(iso))!;
        Assert.Equal(4L * s + 100, files.SizeOf("install.esd"));
        var whole = files.Read("install.esd")!;
        Assert.Equal(4 * s + 100, whole.Length);
        Assert.Equal((byte)'a', whole[2 * s - 1]);
        Assert.Equal((byte)'b', whole[2 * s]);
        Assert.Equal((byte)'c', whole[4 * s]);
        Assert.Equal("abbbb"u8.ToArray(), files.ReadAt("install.esd", 2 * s - 1, 5)); // across a seam
        Assert.Equal("bcc"u8.ToArray(), files.ReadAt("install.esd", 4 * s - 1, 3));
        Assert.Null(files.ReadAt("install.esd", 4 * s + 99, 2)); // past the end
        Assert.Equal("hello"u8.ToArray(), files.Read("other.txt")); // the record after the pieces is its own file
    }
}

/// <summary>
/// The real thing, opt-in (network, ~1 minute, 10 GB of scratch): Microsoft's live catalog, then the disc built by
/// the app's own code. VMQONTROL_WINDOWS_ESD=path to the en-us consumer .esd already downloaded (linked in, so the test
/// does not download 4.4 GB again; its SHA-1 is still checked against the catalog).
/// </summary>
public sealed class WindowsDownloadIntegrationTests
{
    [Trait("Tier", "Integration")]
    [Fact]
    public async Task Microsofts_file_becomes_a_disc_with_the_chosen_edition()
    {
        if (Environment.GetEnvironmentVariable("VMQONTROL_WINDOWS_ESD") is not { Length: > 0 } esd) return;
        using var profile = new TestProfile("mswin-disc");
        var files = await WindowsDownload.LoadAsync(profile.Paths, "11", TestContext.Current.CancellationToken);
        var pro = WindowsDownload.Editions(files, "en-US").Single(e => e.EditionId == "Professional");
        Assert.Equal(new FileInfo(esd).Length, pro.File.Size);
        var library = profile.Paths.DefaultLibrary;
        Directory.CreateDirectory(Path.Combine(library, ".discs"));
        var linked = Path.Combine(library, ".discs", pro.File.FileName);
        using (var ln = System.Diagnostics.Process.Start("ln", [esd, linked])!) await ln.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(File.Exists(linked));
        var steps = new List<string>();
        var iso = await WindowsDownload.BuildDiscAsync(pro, library, new SyncProgress(p => steps.Add(p.Status)), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(esd)); // only the link went
        Assert.False(File.Exists(linked));
        var w = WindowsMedia.Inspect(iso)!;
        Assert.Equal("11", w.Release);
        var only = Assert.Single(w.Editions);
        Assert.Equal("Professional", only.EditionId);
        Assert.Contains("Checking the download…", steps); // the SHA-1 was checked
        Assert.Contains("Writing the installer disc…", steps);
        using (var disc = IsoFiles.Open(iso)!)
        {
            Assert.True(disc.Exists("efi/microsoft/boot/efisys_noprompt.bin"));
            Assert.True(disc.SizeOf("sources/boot.wim") > 100_000_000);
        }

        // A second machine reuses the disc.
        steps.Clear();
        Assert.Equal(iso, await WindowsDownload.BuildDiscAsync(pro, library, new SyncProgress(p => steps.Add(p.Status)), TestContext.Current.CancellationToken));
        Assert.Equal(["Using the disc made earlier"], steps);
    }

    private sealed class SyncProgress(Action<DownloadProgress> report) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => report(value);
    }
}

/// <summary>"Lean Windows": the answer file runs the script from the answer disc in the specialize pass.</summary>
public sealed class WindowsLeanTests
{
    [Fact]
    public void Lean_runs_the_script_once_while_windows_sets_itself_up_and_off_leaves_no_trace()
    {
        var choices = new WindowsInstallChoices { UserName = "A", ComputerName = "PC", EditionName = "Windows 11 Pro", Lean = true };
        var xml = System.Xml.Linq.XDocument.Parse(WindowsAnswerFile.Build(choices, "11"));
        System.Xml.Linq.XNamespace u = "urn:schemas-microsoft-com:unattend";
        var specialize = xml.Root!.Elements(u + "settings").Single(s => (string?)s.Attribute("pass") == "specialize");
        var commands = specialize.Descendants(u + "RunSynchronousCommand").ToList();
        Assert.Equal(["1", "2"], commands.Select(c => c.Element(u + "Order")!.Value));
        var path = commands[1].Element(u + "Path")!.Value;
        Assert.Equal(WindowsLean.Command, path);
        Assert.Contains(@"%d:\VMQLEAN.PS1", path);
        Assert.True(path.Length < 260, "RunSynchronous paths are limited to 259 characters");

        var plain = WindowsAnswerFile.Build(choices with { Lean = false }, "11");
        Assert.DoesNotContain("VMQLEAN", plain);
    }

    [Fact]
    public void The_script_keeps_what_people_use_and_cleans_up_after_itself()
    {
        var script = WindowsLean.Script();
        foreach (var kept in new[] { "Microsoft.WindowsStore", "Microsoft.WindowsCalculator", "Microsoft.Windows.Photos", "Microsoft.WindowsNotepad", "Microsoft.Paint", "Microsoft.WindowsTerminal", "Microsoft.SecHealthUI", "Microsoft.MicrosoftEdge" })
            Assert.DoesNotContain($"'{kept}'", script);
        Assert.Contains("'Microsoft.BingNews'", script);
        Assert.Contains("reg.exe unload 'HKU\\VmqDefault'", script); // the default profile is never left loaded
        Assert.Equal(WindowsLean.RemovedApps.Count, WindowsLean.RemovedApps.Distinct().Count());
    }

    [Fact]
    public void The_answer_disc_carries_the_script_only_when_lean()
    {
        using var profile = new TestProfile("lean-disc");
        foreach (var lean in new[] { true, false })
        {
            var (_, dir) = VmQontrol.Core.Machines.MachineFactory.Create(new VmQontrol.Core.Machines.NewMachineRequest
            {
                Name = $"lean-{lean}",
                LibraryRoot = profile.Paths.DefaultLibrary,
                Guest = new VmQontrol.Core.Machines.GuestConfig { Os = "windows", Release = "11" },
                WindowsEasyInstall = new WindowsInstallChoices { UserName = "A", ComputerName = "PC", EditionName = "Windows 11 Pro", Lean = lean },
            });
            using var disc = IsoFiles.Open(Path.Combine(dir, "unattended.iso"))!;
            Assert.True(disc.Exists("autounattend.xml"));
            var script = disc.Read(WindowsLean.ScriptName);
            if (!lean)
            {
                Assert.Null(script);
                continue;
            }

            Assert.NotNull(script);
            Assert.Equal(Encoding.UTF8.GetPreamble(), script[..3]); // Windows PowerShell 5.1 reads UTF-8 only with the mark
            Assert.Contains("\r\n", Encoding.UTF8.GetString(script));
        }
    }
}
