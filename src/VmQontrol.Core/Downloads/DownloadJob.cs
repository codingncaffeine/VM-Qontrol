using System.Diagnostics;
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
public sealed class DownloadJob(CatalogueEntry entry, string libraryRoot, string machineName)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public CatalogueEntry Entry { get; } = entry;

    public async Task<(MachineConfig Machine, string Directory)> RunAsync(IProgress<DownloadProgress> progress, CancellationToken ct)
    {
        var staging = Path.Combine(libraryRoot, ".downloads", StagingName(Entry));
        Directory.CreateDirectory(staging);
        try
        {
            progress.Report(new DownloadProgress(0, null, "Finding the download…"));
            var expected = await ExpectedBytesAsync(ct).ConfigureAwait(false);
            var args = new List<string> { Entry.Os, Entry.Release };
            if (Entry.Option.Length > 0) args.Add(Entry.Option);
            var start = new ProcessStartInfo(Catalogue.Program) { WorkingDirectory = staging, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
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
    internal static string StagingName(CatalogueEntry entry)
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

    private async Task<long?> ExpectedBytesAsync(CancellationToken ct)
    {
        var args = new List<string> { "--url", Entry.Os, Entry.Release };
        if (Entry.Option.Length > 0) args.Add(Entry.Option);
        var text = await Catalogue.RunAsync(ct, [.. args]).ConfigureAwait(false);
        long total = 0;
        // quickget prints "name: url" lines.
        foreach (var url in System.Text.RegularExpressions.Regex.Matches(text, @"https?://\S+").Select(m => m.Value).Distinct().Take(4))
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

        return total > 0 ? total : null;
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
