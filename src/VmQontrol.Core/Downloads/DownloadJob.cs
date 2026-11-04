using System.Diagnostics;
using System.Security.Cryptography;
using VmQontrol.Core.Import;
using VmQontrol.Core.Machines;

namespace VmQontrol.Core.Downloads;

public sealed record DownloadProgress(long Bytes, long? ExpectedBytes, string Status);

/// <summary>The download ended without a usable machine; the reasons are plain sentences.</summary>
public sealed class DownloadFailedException(IReadOnlyList<string> reasons) : Exception(string.Join(" ", reasons))
{
    public IReadOnlyList<string> Reasons { get; } = reasons;
}

/// <summary>
/// Downloads an operating system through quickget into a staging folder beside the library, verifies every file it
/// produced (quickget can report success while saving an error page), and turns the result into a VM Qontrol machine.
/// Progress is measured from the files growing on disk against the size the server reports.
/// </summary>
public sealed class DownloadJob(CatalogEntry entry, string libraryRoot, string machineName)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public CatalogEntry Entry { get; } = entry;

    public async Task<(MachineConfig Machine, string Directory)> RunAsync(IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var staging = Path.Combine(libraryRoot, ".downloads", StagingName(Entry));
        Directory.CreateDirectory(staging);
        try
        {
            progress.Report(new DownloadProgress(0, null, "Finding the download…"));
            var (expected, urls) = await ExpectedBytesAsync(ct).ConfigureAwait(false);
            var args = new List<string> { Entry.Os, Entry.Release };
            if (Entry.Option.Length > 0) args.Add(Entry.Option);
            var start = new ProcessStartInfo(Catalog.Program) { WorkingDirectory = staging, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) start.ArgumentList.Add(a);
            using var process = Process.Start(start) ?? throw new DownloadFailedException(["quickget could not be started."]);
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var errors = process.StandardError.ReadToEndAsync(ct);
            using (ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }))
            {
                while (!process.HasExited)
                {
                    progress.Report(new DownloadProgress(FolderBytes(staging), expected, "Downloading…"));
                    await Task.WhenAny(process.WaitForExitAsync(ct), Task.Delay(500, ct)).ConfigureAwait(false);
                }
            }

            ct.ThrowIfCancellationRequested();
            var log = (await output.ConfigureAwait(false)) + (await errors.ConfigureAwait(false));
            await File.WriteAllTextAsync(Path.Combine(staging, "quickget.log"), log, ct).ConfigureAwait(false);
            progress.Report(new DownloadProgress(FolderBytes(staging), expected, "Checking the files…"));

            var conf = Directory.GetFiles(staging, "*.conf").FirstOrDefault()
                       ?? throw new DownloadFailedException([$"quickget made no machine. Its last words: {Tail(log)}"]);
            var import = QuickemuImporter.Import(conf);
            var problems = MachineChecks.Problems(import.Machine, import.MachineDirectory).ToList();
            if (Entry.Os == "windows" && log.Contains("Microsoft blocked", StringComparison.OrdinalIgnoreCase))
                problems.Insert(0, "Microsoft refused the automatic download. Download Windows from Microsoft's site in your browser, then choose the file.");
            if (problems.Count > 0) throw new DownloadFailedException(problems);
            foreach (var disc in import.Machine.Media.Where(x => x.Role == MediaRole.Installer && File.Exists(x.File)))
                await CheckDiscSignatureAsync(Entry, urls, disc.File, progress, ct).ConfigureAwait(false);

            var result = Adopt(import, libraryRoot, machineName);
            progress.Report(new DownloadProgress(FolderBytes(result.Directory), expected, "Ready"));
            return result;
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                var parent = Path.Combine(libraryRoot, ".downloads");
                if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The staging folder's name: the entry, then a unique tail, at most 48 characters (a short entry is not padded).</summary>
    internal static string StagingName(CatalogEntry entry)
    {
        var name = $"{entry.Os}-{entry.Release}-{entry.Option}-{Guid.NewGuid():N}";
        return name[..Math.Min(name.Length, 48)].TrimEnd('-');
    }

    /// <summary>Moves a downloaded quickemu machine's folder into the library as a VM Qontrol machine with relative paths.</summary>
    public static (MachineConfig Machine, string Directory) Adopt(QuickemuImport import, string libraryRoot, string name)
    {
        Directory.CreateDirectory(libraryRoot);
        var target = MachineFactory.FolderFor(libraryRoot, name);
        Directory.Move(import.MachineDirectory, target);
        string Move(string path) => path.StartsWith(import.MachineDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? Path.GetRelativePath(import.MachineDirectory, path)
            : path;
        var m = import.Machine;
        var machine = m with
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            ImportedFrom = null,
            Disks = m.Disks.Select(d => d with { File = Move(d.File) }).ToList(),
            Media = m.Media.Where(x => x.Role != MediaRole.Unattended || File.Exists(x.File)).Select(x => x with { File = Move(x.File) }).ToList(),
            Firmware = m.Firmware with { VarsFile = Move(m.Firmware.VarsFile), CodeFile = m.Firmware.CodeFile is { } c ? Move(c) : null },
        };
        MachineStore.Save(machine, Path.Combine(target, MachineStore.FileName));
        return (machine, target);
    }

    private static readonly HashSet<string> UbuntuFamily =
        ["ubuntu", "ubuntu-server", "edubuntu", "kubuntu", "lubuntu", "ubuntu-budgie", "ubuntu-mate", "ubuntu-unity", "ubuntucinnamon", "ubuntukylin", "ubuntustudio", "xubuntu"];

    /// <summary>The publishers in the catalog that sign the checksum files beside their installer discs.</summary>
    internal static SigningPublisher? PublisherFor(string os) => os switch
    {
        "debian" => Signatures.Debian,
        "fedora" => Signatures.Fedora,
        _ when UbuntuFamily.Contains(os) => Signatures.UbuntuDiscs,
        _ => null,
    };

    /// <summary>
    /// quickget checks the checksum a system publishes, not its signature. For Ubuntu (every flavor), Debian and Fedora,
    /// which sign the checksum file beside each disc (measured over all 155 of their release entries in quickget 4.9.9's
    /// catalog), the signed file is read from where quickget downloaded, its signature checked, and the disc's own hash
    /// must be the one it lists. Daily builds are not signed there; they are let through with a note in the log.
    /// </summary>
    internal static async Task CheckDiscSignatureAsync(CatalogEntry entry, IReadOnlyList<string> urls, string disc, IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        if (PublisherFor(entry.Os) is not { } publisher) return;
        var what = $"{entry.DisplayName} {entry.Release}";
        if (urls.FirstOrDefault(u => u.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) is not { } url)
        {
            Log.Warn($"{what}: quickget named no disc address, so {publisher.Name}'s signature could not be looked for.");
            return;
        }

        var cut = url.LastIndexOf('/') + 1;
        var (folder, name) = (url[..cut], Uri.UnescapeDataString(url[cut..]));
        progress.Report(new DownloadProgress(0, null, $"Checking {publisher.Name}'s signature…"));
        string? sums;
        try
        {
            sums = await SignedSumsAsync(publisher, folder, what, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound && entry.Release.StartsWith("daily", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn($"{what}: a daily build, which {publisher.Name} does not sign there; quickget's checksum decided.");
            return;
        }
        catch (HttpRequestException ex)
        {
            throw new DownloadFailedException([$"{publisher.Name}'s signed checksum list could not be read beside the disc ({ex.Message}), so the download could not be checked. Try again."]);
        }

        if (sums is null) return; // gpgv is not installed (logged)
        var listed = Signatures.HashFor(sums, name) ?? throw new DownloadFailedException([$"{publisher.Name}'s signed checksum list does not list {name}, so the disc was not used."]);
        await using var stream = new FileStream(disc, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var hash = Convert.ToHexStringLower(listed.Length == 128 ? await SHA512.HashDataAsync(stream, ct).ConfigureAwait(false) : await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        if (hash != listed)
            throw new DownloadFailedException([$"The disc does not match the checksum {publisher.Name} signed for {name}, so it was not used."]);
        Log.Info($"{what}: {name} matches {publisher.Name}'s signed checksum.");
    }

    /// <summary>The signed text of the checksum file in <paramref name="folder"/>, or null when gpgv is not installed.</summary>
    private static async Task<string?> SignedSumsAsync(SigningPublisher publisher, string folder, string what, CancellationToken ct)
    {
        if (publisher == Signatures.Fedora)
        {
            var listing = await Http.GetStringAsync(folder, ct).ConfigureAwait(false);
            var checksum = System.Text.RegularExpressions.Regex.Match(listing, "href=\"(?:\\./)?([^\"/?#]+-CHECKSUM)\"");
            if (!checksum.Success) throw new HttpRequestException("no CHECKSUM file", null, System.Net.HttpStatusCode.NotFound);
            var (check, text) = await Signatures.VerifyClearsignedAsync(publisher, await Http.GetByteArrayAsync(folder + checksum.Groups[1].Value, ct).ConfigureAwait(false), $"{what}'s checksum list", ct).ConfigureAwait(false);
            return check.Checked ? text : null;
        }

        var (sumsName, signatureName) = publisher == Signatures.Debian ? ("SHA512SUMS", "SHA512SUMS.sign") : ("SHA256SUMS", "SHA256SUMS.gpg");
        var sums = await Http.GetByteArrayAsync(folder + sumsName, ct).ConfigureAwait(false);
        var signature = await Http.GetByteArrayAsync(folder + signatureName, ct).ConfigureAwait(false);
        var work = Directory.CreateTempSubdirectory("vmqontrol-sums-").FullName;
        try
        {
            var file = Path.Combine(work, sumsName);
            await File.WriteAllBytesAsync(file, sums, ct).ConfigureAwait(false);
            var verdict = await Signatures.VerifyDetachedAsync(publisher, signature, file, $"{what}'s checksum list", ct).ConfigureAwait(false);
            return verdict.Checked ? System.Text.Encoding.UTF8.GetString(sums) : null;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    private async Task<(long? Bytes, IReadOnlyList<string> Urls)> ExpectedBytesAsync(CancellationToken ct)
    {
        var args = new List<string> { "--url", Entry.Os, Entry.Release };
        if (Entry.Option.Length > 0) args.Add(Entry.Option);
        var text = await Catalog.RunAsync(ct, [.. args]).ConfigureAwait(false);
        long total = 0;
        // quickget prints "name: url" lines.
        var urls = System.Text.RegularExpressions.Regex.Matches(text, @"https?://\S+").Select(m => m.Value).Distinct().Take(4).ToList();
        foreach (var url in urls)
        {
            try
            {
                using var head = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), ct).ConfigureAwait(false);
                if (head.Content.Headers.ContentLength is { } length) total += length;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
            }
        }

        return (total > 0 ? total : null, urls);
    }

    private static long FolderBytes(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static string Tail(string log)
    {
        var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => !l.Contains('#')).TakeLast(3);
        return string.Join(" ", lines);
    }
}
