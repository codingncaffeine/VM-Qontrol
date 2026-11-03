using System.Formats.Tar;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VmQontrol.Core.Import;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Core.Machines;

/// <summary>
/// A machine taken elsewhere (phase 11, D30): its folder copied as a portable machine (disks standalone, firmware
/// variables, TPM, history) with manifest.json (SHA-256 of every file), as a folder or one .tar; and a quickemu .conf for
/// people going back to quickemu. The machine itself is only read.
/// </summary>
public static class MachineExport
{
    public const string ManifestName = "manifest.json";

    /// <summary>Files of a machine folder that belong to this computer only and never travel.</summary>
    private static readonly string[] Local = [MachineSuspend.StateFile, MachineSuspend.CommandFile, ManifestName];

    /// <summary>
    /// Copies the machine (which must be off) into a new folder in <paramref name="destination"/>, or into one .tar there.
    /// Returns the folder or file made and what the person should know. Call on a worker.
    /// </summary>
    public static async Task<(string Path, IReadOnlyList<string> Notes)> ExportAsync(MachineConfig m, string directory, string destination, bool asTar,
        IProgress<ImportProgress> progress, CancellationToken ct)
    {
        string Full(string f) => Path.IsPathRooted(f) ? f : Path.Combine(directory, f);
        var notes = new List<string>();
        var target = MachineFactory.FolderFor(destination, m.Name);
        Directory.CreateDirectory(target);
        try
        {
            // Disks: a standalone disk is copied as it is (its snapshots travel too); a linked clone's is made whole.
            var disks = new List<DiskConfig>();
            for (var i = 0; i < m.Disks.Count; i++)
            {
                var d = m.Disks[i];
                var file = $"disk{i}.qcow2";
                var source = Full(d.File);
                var index = i;
                var report = new Progress<double>(p => progress.Report(new ImportProgress(index + 1, m.Disks.Count, p, $"Copying disk {index + 1} of {m.Disks.Count}…")));
                if (!File.Exists(source)) notes.Add($"Disk {i + 1} was never made (the machine has not been started); the copy makes it at its first power on.");
                else if (DiskState.BackingFile(source) is not null || d.Format != "qcow2")
                {
                    if (DiskState.BackingFile(source) is not null) notes.Add($"Disk {i + 1} was a linked clone's; the copy holds it whole, without its snapshots.");
                    await MachineImporter.ConvertAsync(source, Path.Combine(target, file), report, ct).ConfigureAwait(false);
                }
                else await CopyAsync(source, Path.Combine(target, file), report, ct).ConfigureAwait(false);
                disks.Add(d with { File = file, Format = "qcow2" });
            }

            // The rest of the folder: firmware variables, TPM state, answer discs, settings history.
            var diskFiles = m.Disks.Select(d => Path.GetFullPath(Full(d.File))).ToHashSet(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(directory, file);
                if (diskFiles.Contains(Path.GetFullPath(file)) || Local.Contains(relative) || relative == MachineStore.FileName) continue;
                var to = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(file, to);
            }

            if (MachineSuspend.IsSuspended(directory)) notes.Add("The machine was suspended; the copy starts afresh (its saved state stays on this computer).");
            var media = new List<MediaConfig>();
            foreach (var x in m.Media)
            {
                if (!Path.IsPathRooted(x.File)) { media.Add(x); continue; }
                notes.Add($"{Path.GetFileName(x.File)} lives outside the machine's folder and was left out; put it in the copy's drive again where needed.");
            }

            var exported = m with
            {
                // A new identity, so the copy and the original can both be listed (and run) on one computer; the MAC travels.
                Id = Guid.NewGuid().ToString("N"),
                Disks = disks,
                Media = media,
                KernelBoot = m.KernelBoot is { } kb && !Path.IsPathRooted(kb.Kernel) ? kb : null,
                ImportedFrom = null,
                Notes = m.Notes,
            };
            MachineStore.Save(exported, Path.Combine(target, MachineStore.FileName));
            progress.Report(new ImportProgress(m.Disks.Count, m.Disks.Count, 100, "Writing the manifest (a checksum of every file)…"));
            await WriteManifestAsync(target, m.Name, ct).ConfigureAwait(false);

            if (!asTar) return (target, notes);
            var tar = Path.Combine(destination, Path.GetFileName(target) + ".tar");
            progress.Report(new ImportProgress(m.Disks.Count, m.Disks.Count, 100, "Packing it into one file…"));
            await using (var stream = new FileStream(tar, FileMode.CreateNew))
                await TarFile.CreateFromDirectoryAsync(target, stream, includeBaseDirectory: true, ct).ConfigureAwait(false);
            Directory.Delete(target, recursive: true);
            return (tar, notes);
        }
        catch
        {
            try { Directory.Delete(target, recursive: true); } catch (IOException) { }
            throw;
        }
    }

    private static async Task CopyAsync(string from, string to, IProgress<double> progress, CancellationToken ct)
    {
        await using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        await using var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        var buffer = new byte[4 << 20];
        long done = 0, total = Math.Max(1, input.Length);
        int read;
        while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            done += read;
            progress.Report(done * 100.0 / total);
        }
    }

    private static async Task WriteManifestAsync(string folder, string name, CancellationToken ct)
    {
        var files = new JsonArray();
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            await using var stream = File.OpenRead(file);
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
            files.Add(new JsonObject { ["path"] = Path.GetRelativePath(folder, file), ["bytes"] = new FileInfo(file).Length, ["sha256"] = hash });
        }

        var manifest = new JsonObject
        {
            ["exportedBy"] = $"VM Qontrol {BuildInfo.Version}",
            ["exported"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            ["machine"] = name,
            ["files"] = files,
        };
        await File.WriteAllTextAsync(Path.Combine(folder, ManifestName), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
    }

    /// <summary>Every file the manifest names is there with its checksum; the problems, in words (empty = whole).</summary>
    public static async Task<IReadOnlyList<string>> VerifyAsync(string folder, CancellationToken ct)
    {
        var problems = new List<string>();
        var path = Path.Combine(folder, ManifestName);
        if (!File.Exists(path)) return ["The folder has no manifest.json: it was not exported by VM Qontrol."];
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
        foreach (var f in manifest?["files"]?.AsArray() ?? [])
        {
            var file = Path.Combine(folder, f!["path"]!.GetValue<string>());
            if (!File.Exists(file)) { problems.Add($"{f["path"]} is missing."); continue; }
            await using var stream = File.OpenRead(file);
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)) != f["sha256"]!.GetValue<string>())
                problems.Add($"{f["path"]} is damaged (its checksum differs).");
        }

        return problems;
    }

    /// <summary>
    /// An exported .tar into <paramref name="libraryRoot"/>: unpacked into a folder of its own, checked against its
    /// manifest (a damaged copy is removed again), and ready to list. Call on a worker.
    /// </summary>
    public static async Task<string> UnpackAsync(string tar, string libraryRoot, CancellationToken ct)
    {
        var staging = Path.Combine(libraryRoot, ".importing", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(staging);
        try
        {
            await using (var stream = File.OpenRead(tar))
                await TarFile.ExtractToDirectoryAsync(stream, staging, overwriteFiles: false, ct).ConfigureAwait(false);
            var inner = Directory.GetDirectories(staging) is [var only] ? only : throw new InvalidDataException("The file does not hold one machine folder.");
            if (await VerifyAsync(inner, ct).ConfigureAwait(false) is { Count: > 0 } problems) throw new InvalidDataException(string.Join(" ", problems));
            var machine = MachineStore.Load(Path.Combine(inner, MachineStore.FileName));
            var target = MachineFactory.FolderFor(libraryRoot, machine.Name);
            Directory.Move(inner, target);
            return target;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    /// <summary>
    /// A quickemu .conf beside the machine's folder that runs the same disk with quickemu (named after the folder, so
    /// quickemu finds OVMF_VARS.fd beside the disk). Returns the file written.
    /// </summary>
    public static string WriteQuickemuConf(MachineConfig m, string directory)
    {
        var folder = Path.GetFileName(directory.TrimEnd('/'));
        var parent = Path.GetDirectoryName(directory.TrimEnd('/'))!;
        string Rel(string f) => Path.GetRelativePath(parent, Path.IsPathRooted(f) ? f : Path.Combine(directory, f));
        var lines = new List<string>
        {
            "#!/usr/bin/quickemu --vm",
            $"# Written by VM Qontrol {BuildInfo.Version} for {m.Name}.",
            $"guest_os=\"{m.Guest.Os}\"",
            $"disk_img=\"{Rel(m.Disks[0].File)}\"",
            $"disk_size=\"{m.Disks[0].SizeGiB}G\"",
        };
        if (m.Media.FirstOrDefault(x => x.Role == MediaRole.Installer && x.Connect != Connect.Never) is { } iso) lines.Add($"iso=\"{Rel(iso.File)}\"");
        if (m.Media.FirstOrDefault(x => x.Role == MediaRole.Drivers) is { } fixedIso) lines.Add($"fixed_iso=\"{Rel(fixedIso.File)}\"");
        if (m.MemoryMiB is { } mem) lines.Add($"ram=\"{(mem % 1024 == 0 ? $"{mem / 1024}G" : $"{mem}M")}\"");
        if (m.Cpu.Cores is { } cores) lines.Add($"cpu_cores=\"{cores}\"");
        lines.Add($"boot=\"{(m.Firmware.Kind == FirmwareKind.Uefi ? "efi" : "legacy")}\"");
        if (m.Firmware.SecureBoot) lines.Add("secureboot=\"on\"");
        lines.Add($"tpm=\"{(m.Tpm ? "on" : "off")}\"");
        if (m.Nics.FirstOrDefault()?.Mac is { } mac) lines.Add($"macaddr=\"{mac}\"");
        if (m.Input.Layout is { Length: > 0 } layout) lines.Add($"keyboard_layout=\"{layout}\"");
        var conf = Path.Combine(parent, folder + ".conf");
        File.WriteAllText(conf, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        return conf;
    }
}
