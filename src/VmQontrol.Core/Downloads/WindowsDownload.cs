using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;
using VmQontrol.Core.Media;

namespace VmQontrol.Core.Downloads;

/// <summary>One Windows image file in Microsoft's Media Creation Tool catalog: one language and architecture, with
/// every edition it carries (a consumer file holds Home, Pro, Education…; a business file holds Enterprise).</summary>
public sealed record WindowsImageFile(string Release, string Language, string Architecture, bool Business, long Size, string Sha1, string Url, string FileName, IReadOnlyList<string> EditionIds)
{
    /// <summary>"26100.4349" from the file name.</summary>
    public string Build => Regex.Match(FileName, @"^\d+\.\d+") is { Success: true } m ? m.Value : "";
}

/// <summary>An edition a person can pick, and the file that carries it.</summary>
public sealed record WindowsDownloadEdition(string EditionId, string Name, WindowsImageFile File);

/// <summary>
/// Windows from Microsoft's own catalog, the one its Media Creation Tool reads (D28): a small cabinet at a fixed
/// go.microsoft.com link lists every language, architecture and edition of the current release, each as one .esd file
/// on Microsoft's download network with its SHA-1. That network is not behind the bot check that refuses scripted
/// downloads from Microsoft's web page. The .esd becomes an installer disc with wimlib and xorriso (<see cref="BuildDiscAsync"/>).
/// </summary>
public static class WindowsDownload
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    public static readonly IReadOnlyDictionary<string, string> CatalogLinks = new Dictionary<string, string>
    {
        ["11"] = "https://go.microsoft.com/fwlink/?LinkId=2156292",
        ["10"] = "https://go.microsoft.com/fwlink/?LinkId=841361",
    };

    /// <summary>The programs that turn the download into a disc; their absence is said before anything is downloaded.</summary>
    public static IReadOnlyList<string> MissingTools(Func<string, bool>? onPath = null)
    {
        onPath ??= Hosts.Doctor.OnPath;
        var missing = new List<string>();
        if (!onPath("wimlib-imagex")) missing.Add("wimlib-imagex");
        if (!onPath("xorriso")) missing.Add("xorriso");
        if (!onPath("bsdtar") && !onPath("cabextract")) missing.Add("bsdtar");
        return missing;
    }

    /// <summary>The catalog for a release ("11" or "10"), cached for a day; an old copy is used when offline.</summary>
    public static async Task<IReadOnlyList<WindowsImageFile>> LoadAsync(QontrolPaths paths, string release, CancellationToken ct = default)
    {
        var cache = Path.Combine(paths.Cache, $"windows-{release}-products.xml");
        if (!File.Exists(cache) || File.GetLastWriteTimeUtc(cache) < DateTime.UtcNow.AddDays(-1))
        {
            try
            {
                var xml = await FetchCatalogAsync(paths, CatalogLinks[release], ct).ConfigureAwait(false);
                Directory.CreateDirectory(paths.Cache);
                await File.WriteAllTextAsync(cache, xml, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (!File.Exists(cache)) throw new DownloadFailedException([$"Microsoft's Windows catalog could not be read: {ex.Message}"]);
            }
        }

        return Parse(await File.ReadAllTextAsync(cache, ct).ConfigureAwait(false), release);
    }

    private static async Task<string> FetchCatalogAsync(QontrolPaths paths, string link, CancellationToken ct)
    {
        var work = Path.Combine(paths.Cache, $"windows-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            var cab = Path.Combine(work, "products.cab");
            await using (var body = await Http.GetStreamAsync(link, ct).ConfigureAwait(false))
            await using (var file = File.Create(cab))
                await body.CopyToAsync(file, ct).ConfigureAwait(false);
            var head = new byte[4];
            await using (var f = File.OpenRead(cab)) f.ReadExactly(head);
            if (!head.AsSpan().SequenceEqual("MSCF"u8)) throw new InvalidDataException("the link did not give Microsoft's catalog file");
            var (tool, args) = Hosts.Doctor.OnPath("bsdtar") ? ("bsdtar", new[] { "-xf", cab, "-C", work }) : ("cabextract", new[] { "-q", "-d", work, cab });
            var (code, output) = await RunAsync(tool, args, null, ct).ConfigureAwait(false);
            var xml = Path.Combine(work, "products.xml");
            if (code != 0 || !File.Exists(xml)) throw new InvalidDataException($"{tool} could not open the catalog: {Last(output)}");
            return await File.ReadAllTextAsync(xml, ct).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    public static IReadOnlyList<WindowsImageFile> Parse(string xml, string release)
    {
        var doc = new XmlDocument { XmlResolver = null };
        doc.LoadXml(xml);
        var files = new List<(string Lang, string Arch, bool Business, long Size, string Sha1, string Url, string Name, string Edition)>();
        foreach (XmlElement f in doc.GetElementsByTagName("File"))
        {
            string T(string name) => f[name]?.InnerText.Trim() ?? "";
            var name = T("FileName");
            // A plain file name only: it becomes a file in the discs folder.
            if (Path.GetFileName(name) != name || name.StartsWith('.')) continue;
            if (!name.EndsWith(".esd", StringComparison.OrdinalIgnoreCase) || !long.TryParse(T("Size"), NumberStyles.None, CultureInfo.InvariantCulture, out var size)) continue;
            var url = T("FilePath");
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !uri.Host.EndsWith(".microsoft.com", StringComparison.OrdinalIgnoreCase)) continue;
            files.Add((T("LanguageCode").ToLowerInvariant(), T("Architecture").ToLowerInvariant(), name.Contains("CLIENTBUSINESS", StringComparison.OrdinalIgnoreCase), size, T("Sha1").ToLowerInvariant(), url, name, T("Edition")));
        }

        return files.GroupBy(f => f.Name)
            .Select(g => new WindowsImageFile(release, g.First().Lang, g.First().Arch, g.First().Business, g.First().Size, g.First().Sha1, g.First().Url, g.Key,
                g.Select(f => f.Edition).Where(e => e.Length > 0).Distinct().ToList()))
            .ToList();
    }

    /// <summary>The editions on offer for a language (falls back to English when Microsoft has no file in it).</summary>
    public static IReadOnlyList<WindowsDownloadEdition> Editions(IReadOnlyList<WindowsImageFile> files, string language, string architecture = "x64")
    {
        var lang = language.ToLowerInvariant();
        var mine = files.Where(f => f.Architecture == architecture).ToList();
        if (!mine.Any(f => f.Language == lang)) lang = mine.Any(f => f.Language.StartsWith(lang.Split('-')[0] + "-", StringComparison.Ordinal))
            ? mine.First(f => f.Language.StartsWith(lang.Split('-')[0] + "-", StringComparison.Ordinal)).Language
            : "en-us";
        var result = new List<WindowsDownloadEdition>();
        foreach (var file in mine.Where(f => f.Language == lang).OrderBy(f => f.Business))
            foreach (var id in file.EditionIds)
                if (EditionNames.TryGetValue(id, out var name))
                    result.Add(new WindowsDownloadEdition(id, $"Windows {file.Release} {name}", file));
        return result.DistinctBy(e => e.EditionId).ToList();
    }

    /// <summary>Microsoft's edition ids and the names Windows shows. Legacy ids the catalog still lists (Starter,
    /// HomeBasic, Ultimate…) are not in the file and are left out.</summary>
    public static readonly IReadOnlyDictionary<string, string> EditionNames = new Dictionary<string, string>
    {
        ["Professional"] = "Pro",
        ["ProfessionalN"] = "Pro N",
        ["Core"] = "Home",
        ["CoreN"] = "Home N",
        ["CoreSingleLanguage"] = "Home Single Language",
        ["Education"] = "Education",
        ["EducationN"] = "Education N",
        ["ProfessionalEducation"] = "Pro Education",
        ["ProfessionalEducationN"] = "Pro Education N",
        ["ProfessionalWorkstation"] = "Pro for Workstations",
        ["ProfessionalWorkstationN"] = "Pro for Workstations N",
        ["Enterprise"] = "Enterprise",
        ["EnterpriseN"] = "Enterprise N",
    };

    /// <summary>
    /// The installer disc for one edition, kept in the library's .discs folder and reused by later machines: the .esd is
    /// downloaded (resuming a partial one), checked against Microsoft's SHA-1, then made into a disc with only that
    /// edition. The .esd is removed once the disc exists.
    /// </summary>
    public static async Task<string> BuildDiscAsync(WindowsDownloadEdition edition, string libraryRoot, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        if (MissingTools() is { Count: > 0 } missing)
            throw new DownloadFailedException([$"Making the Windows disc needs {string.Join(" and ", missing)}. Install them with: {Hosts.Doctor.InstallCommand(Hosts.Doctor.DetectThisDistro(), "windows-download")}"]);
        var file = edition.File;
        var discs = Path.Combine(libraryRoot, ".discs");
        Directory.CreateDirectory(discs);
        var iso = Path.Combine(discs, $"Windows {file.Release} {EditionNames[edition.EditionId]} {file.Build} {file.Language} {file.Architecture}.iso");
        if (File.Exists(iso) && WindowsMedia.Inspect(iso)?.Editions.Any(e => e.EditionId == edition.EditionId) == true)
        {
            progress.Report(new DownloadProgress(new FileInfo(iso).Length, new FileInfo(iso).Length, "Using the disc made earlier"));
            return iso;
        }

        var esd = Path.Combine(discs, file.FileName);
        await DownloadAsync(file, esd, progress, ct).ConfigureAwait(false);

        progress.Report(new DownloadProgress(file.Size, file.Size, "Making the installer disc…"));
        var images = WindowsMedia.EditionsInFile(esd) ?? throw new DownloadFailedException(["The downloaded file is not a Windows image."]);
        var image = images.FirstOrDefault(i => i.EditionId == edition.EditionId)
                    ?? throw new DownloadFailedException([$"Microsoft's file has no {edition.Name}. It holds: {string.Join(", ", images.Skip(3).Select(i => i.DisplayName))}."]);
        var stage = Path.Combine(discs, $".build-{Guid.NewGuid():N}");
        var partial = iso + ".part";
        try
        {
            Directory.CreateDirectory(stage);
            // Image 1: the disc's files. 2 and 3: Windows PE and Setup, which make boot.wim. Then the one edition, copied
            // as it is compressed (seconds; recompressing a single edition takes many CPU-minutes for 4% less).
            await Wim(ct, "apply", esd, "1", stage).ConfigureAwait(false);
            var bootWim = Path.Combine(stage, "sources", "boot.wim");
            await Wim(ct, "export", esd, "2", bootWim, "--compress=LZX").ConfigureAwait(false);
            await Wim(ct, "export", esd, "3", bootWim, "--boot").ConfigureAwait(false);
            await Wim(ct, "export", esd, image.Index.ToString(CultureInfo.InvariantCulture), Path.Combine(stage, "sources", "install.esd"), "--compress=LZMS", "--solid").ConfigureAwait(false);
            var label = $"CCCOMA_{(file.Architecture == "arm64" ? "A64" : "X64")}FRE_{file.Language.ToUpperInvariant()}_DV9";
            // ISO 9660 level 3 + Joliet: install.esd is over 4 GiB, which level 3 stores in pieces (Windows Setup reads it).
            // BIOS boots etfsboot.com; UEFI boots efisys_noprompt.bin, which does not wait for a key.
            var efi = File.Exists(Path.Combine(stage, "efi", "microsoft", "boot", "efisys_noprompt.bin")) ? "efi/microsoft/boot/efisys_noprompt.bin" : "efi/microsoft/boot/efisys.bin";
            var args = new List<string> { "-as", "mkisofs", "-iso-level", "3", "-J", "-joliet-long", "-V", label };
            if (File.Exists(Path.Combine(stage, "boot", "etfsboot.com"))) args.AddRange(["-b", "boot/etfsboot.com", "-no-emul-boot", "-boot-load-size", "8", "-eltorito-alt-boot"]);
            args.AddRange(["-e", efi, "-no-emul-boot", "-o", partial, stage]);
            progress.Report(new DownloadProgress(file.Size, file.Size, "Writing the installer disc…"));
            var (code, output) = await RunAsync("xorriso", [.. args], null, ct).ConfigureAwait(false);
            if (code != 0) throw new DownloadFailedException([$"xorriso could not write the disc: {Last(output)}"]);
            File.Move(partial, iso, overwrite: true);
            File.Delete(esd);
            return iso;
        }
        finally
        {
            try
            {
                if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true);
                if (File.Exists(partial)) File.Delete(partial);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Downloads the .esd into place, resuming a partial file; the SHA-1 decides, never the server's word.</summary>
    public static async Task DownloadAsync(WindowsImageFile file, string target, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var partial = target + ".part";
        if (File.Exists(target))
        {
            progress.Report(new DownloadProgress(file.Size, file.Size, "Checking the download…"));
            if (await Sha1Async(target, ct).ConfigureAwait(false) == file.Sha1) return;
        }

        for (var attempt = 1; ; attempt++)
        {
            var have = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (have > file.Size) { File.Delete(partial); have = 0; }
            try
            {
                if (have < file.Size)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, file.Url);
                    if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
                    using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    if (have > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent) have = 0; // the server sent it all again
                    await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    await using var output = DownloadWrite.Open(partial, append: have > 0);
                    var buffer = new byte[1 << 20];
                    var last = Stopwatch.StartNew();
                    int read;
                    while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await DownloadWrite.WriteAsync(output, buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                        have += read;
                        if (last.ElapsedMilliseconds >= 250)
                        {
                            progress.Report(new DownloadProgress(have, file.Size, "Downloading Windows from Microsoft…"));
                            last.Restart();
                        }
                    }
                }

                progress.Report(new DownloadProgress(file.Size, file.Size, "Checking the download…"));
                var sha1 = await Sha1Async(partial, ct).ConfigureAwait(false);
                if (sha1 != file.Sha1)
                {
                    File.Delete(partial);
                    throw new DownloadFailedException([$"The download does not match Microsoft's checksum (got {sha1}, expected {file.Sha1}); it was deleted. Try again."]);
                }

                File.Move(partial, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException && attempt < 4 && !ct.IsCancellationRequested)
            {
                progress.Report(new DownloadProgress(have, file.Size, $"The connection dropped; resuming ({attempt} of 3)…"));
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string> Sha1Async(string file, CancellationToken ct)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false));
    }

    private static async Task Wim(CancellationToken ct, params string[] args)
    {
        var (code, output) = await RunAsync("wimlib-imagex", args, null, ct).ConfigureAwait(false);
        if (code != 0) throw new DownloadFailedException([$"wimlib could not {args[0]} the Windows image: {Last(output)}"]);
    }

    private static async Task<(int Code, string Output)> RunAsync(string program, IReadOnlyList<string> args, string? workingDirectory, CancellationToken ct)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (workingDirectory is not null) start.WorkingDirectory = workingDirectory;
        foreach (var a in args) start.ArgumentList.Add(a);
        using var p = Process.Start(start) ?? throw new DownloadFailedException([$"{program} could not be started."]);
        using var kill = ct.Register(() => { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var output = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errors = p.StandardError.ReadToEndAsync(CancellationToken.None);
        await p.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return (p.ExitCode, (await output.ConfigureAwait(false)) + (await errors.ConfigureAwait(false)));
    }

    private static string Last(string output) =>
        string.Join(" ", output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(2));
}
