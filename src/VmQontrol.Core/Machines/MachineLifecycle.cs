using VmQontrol.Core.Runtime;

namespace VmQontrol.Core.Machines;

/// <summary>Cloning and deleting machines. Every operation needs the machine off; disks are copied, never moved.</summary>
public static class MachineLifecycle
{
    /// <summary>
    /// A full clone: a machine of its own with copies of the disks (compacted by qemu-img convert), the firmware
    /// variables and the TPM state, a new id and a new MAC address so both can be on the same network. Files the original
    /// kept in its own folder (discs, answer files) are referenced where they are.
    /// </summary>
    public static async Task<(MachineConfig Machine, string Directory)> CloneAsync(MachineConfig source, string sourceDirectory, string libraryRoot, string name,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        RefuseIfRunning(source);
        string Full(string f) => Path.IsPathRooted(f) ? f : Path.Combine(sourceDirectory, f);
        var target = MachineFactory.FolderFor(libraryRoot, name);
        Directory.CreateDirectory(target);
        try
        {
            var disks = new List<DiskConfig>();
            for (var i = 0; i < source.Disks.Count; i++)
            {
                var d = source.Disks[i];
                var file = $"disk{i}.qcow2";
                if (File.Exists(Full(d.File)))
                {
                    progress?.Report($"Copying disk {i + 1} of {source.Disks.Count}…");
                    await QemuImg.RunAsync(ct, "convert", "-p", "-O", "qcow2", Full(d.File), Path.Combine(target, file)).ConfigureAwait(false);
                }

                disks.Add(d with { File = file, Format = "qcow2" });
            }

            var (clone, _) = Finish(source, sourceDirectory, target, name, disks, Full);
            return (clone, target);
        }
        catch
        {
            TryDelete(target);
            throw;
        }
    }

    /// <summary>The clone's settings: a new id and MAC, its own firmware variables and TPM, the original's discs where they are.</summary>
    private static (MachineConfig Clone, string Directory) Finish(MachineConfig source, string sourceDirectory, string target, string name, List<DiskConfig> disks, Func<string, string> Full)
    {
        var vars = Full(source.Firmware.VarsFile);
        if (File.Exists(vars)) File.Copy(vars, Path.Combine(target, "OVMF_VARS.fd"));
        // A quickemu machine keeps TPM state beside its disk; a VM Qontrol machine in tpm/.
        var tpmState = source.ImportedFrom?.Kind == "quickemu" ? sourceDirectory : Path.Combine(sourceDirectory, "tpm");
        if (source.Tpm && Directory.Exists(tpmState))
        {
            Directory.CreateDirectory(Path.Combine(target, "tpm"));
            foreach (var f in Directory.GetFiles(tpmState, "tpm2-*")) File.Copy(f, Path.Combine(target, "tpm", Path.GetFileName(f)));
        }

        var nic = source.Nics.FirstOrDefault();
        var clone = source with
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name,
            IsTemplate = false,
            ImportedFrom = null,
            Disks = disks,
            Media = source.Media.Select(m => m with { File = Full(m.File) }).ToList(),
            Firmware = source.Firmware with { VarsFile = "OVMF_VARS.fd", CodeFile = source.Firmware.CodeFile is { } c ? Full(c) : null },
            KernelBoot = source.KernelBoot is { } kb ? kb with { Kernel = Full(kb.Kernel), Initrd = kb.Initrd is { } initrd ? Full(initrd) : null } : null,
            Nics = nic is null ? source.Nics : [nic with { Mac = nic.Mac is null ? null : NewMac(), SshHostPort = null }, .. source.Nics.Skip(1)],
            Notes = source.Notes.Length > 0 ? source.Notes : $"{(source.IsTemplate ? "Made from the template" : "Cloned from")} {source.Name} on {DateTime.Now:d}.",
        };
        MachineStore.Save(clone, Path.Combine(target, MachineStore.FileName));
        return (clone, target);
    }

    /// <summary>
    /// Makes the machine a template, or turns a template back into a machine. A template must be off and not suspended:
    /// its machines are made from its disks, and a saved running state would belong to none of them.
    /// </summary>
    public static MachineConfig SetTemplate(MachineConfig machine, string directory, bool template)
    {
        if (machine.ImportedFrom is not null)
            throw new InvalidOperationException($"{machine.Name} is a quickemu machine, which VM Qontrol never changes. Clone it, then make the clone a template.");
        if (template && MachineSuspend.IsSuspended(directory))
            throw new InvalidOperationException($"{machine.Name} is suspended. Resume it and shut it down, or discard its suspended state, before making it a template.");
        var changed = machine with { IsTemplate = template };
        MachineStore.Save(changed, Path.Combine(directory, MachineStore.FileName));
        return changed;
    }

    /// <summary>The library's folder of read-only bases that linked clones are layered on.</summary>
    public const string BasesFolder = ".bases";

    /// <summary>
    /// A linked clone: a machine of its own whose disks are thin layers on a read-only base of the original's disks as
    /// they are now, so it takes seconds and almost no space. The base is made by freezing the original's disk in place
    /// (the original then writes to a new layer of its own, exactly as before); an original whose disk holds snapshots
    /// cannot be frozen without hiding them, so its base is a copy of its current state instead. Bases live in the
    /// library's <see cref="BasesFolder"/>, outlive the machine they came from, and go when no machine uses them.
    /// A quickemu machine's disk is never touched: it always gets the copy.
    /// </summary>
    public static async Task<(MachineConfig Machine, string Directory, MachineConfig Original)> LinkedCloneAsync(MachineConfig source, string sourceDirectory,
        string libraryRoot, string name, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        RefuseIfRunning(source);
        string Full(string f) => Path.IsPathRooted(f) ? f : Path.Combine(sourceDirectory, f);
        var bases = Path.Combine(libraryRoot, BasesFolder);
        Directory.CreateDirectory(bases);
        var target = MachineFactory.FolderFor(libraryRoot, name);
        Directory.CreateDirectory(target);
        var parentDisks = source.Disks.ToList();
        var frozen = new List<(string Disk, string Base)>();
        try
        {
            var disks = new List<DiskConfig>();
            for (var i = 0; i < source.Disks.Count; i++)
            {
                var d = source.Disks[i];
                var file = $"disk{i}.qcow2";
                var disk = Full(d.File);
                if (!File.Exists(disk))
                {
                    disks.Add(d with { File = file, Format = "qcow2" });
                    continue;
                }

                var info = await QemuImg.InfoAsync(disk, ct).ConfigureAwait(false);
                var hasSnapshots = info["snapshots"] is System.Text.Json.Nodes.JsonArray { Count: > 0 };
                var format = (string?)info["format"] ?? d.Format;
                // Unique even for two clones in the same second: a base is never overwritten.
                var stamp = $"{DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture)}-{Guid.NewGuid().ToString("N")[..6]}";
                var baseFile = Path.Combine(bases, $"{source.Id[..Math.Min(12, source.Id.Length)]}-disk{i}-{stamp}.{(hasSnapshots ? "qcow2" : format)}");
                if (hasSnapshots || source.ImportedFrom is not null)
                {
                    progress?.Report($"Copying disk {i + 1} of {source.Disks.Count} as it is now (its snapshots keep it from being shared in place)…");
                    await QemuImg.RunAsync(ct, "convert", "-p", "-O", "qcow2", disk, baseFile).ConfigureAwait(false);
                    format = "qcow2";
                }
                else
                {
                    progress?.Report($"Sharing disk {i + 1} of {source.Disks.Count}…");
                    // A disk that is itself a layer must keep pointing at its base from the new folder (read before moving:
                    // a relative name means something else there).
                    var below = DiskState.BackingFile(disk);
                    File.Move(disk, baseFile);
                    frozen.Add((disk, baseFile));
                    if (below is not null)
                        await QemuImg.RunAsync(ct, "rebase", "-u", "-b", Path.GetRelativePath(bases, below), "-F", (string?)info["backing-filename-format"] ?? "qcow2", baseFile).ConfigureAwait(false);
                    await Overlay(disk, baseFile, format, ct).ConfigureAwait(false);
                    parentDisks[i] = d with { Format = "qcow2" };
                }

                File.SetUnixFileMode(baseFile, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                await Overlay(Path.Combine(target, file), baseFile, format, ct).ConfigureAwait(false);
                disks.Add(d with { File = file, Format = "qcow2" });
            }

            var (clone, _) = Finish(source, sourceDirectory, target, name, disks, Full);
            var original = frozen.Count > 0 ? source with { Disks = parentDisks } : source;
            if (frozen.Count > 0) MachineStore.Save(original, Path.Combine(sourceDirectory, MachineStore.FileName));
            return (clone, target, original);
        }
        catch
        {
            // Put every frozen disk back where it was, as it was: nothing has written to it since.
            foreach (var (disk, baseFile) in frozen)
            {
                File.Delete(disk);
                File.SetUnixFileMode(baseFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                File.Move(baseFile, disk);
            }

            TryDelete(target);
            throw;
        }
    }

    /// <summary>
    /// Deletes the bases no machine is layered on any more; returns the ones deleted. Every library root is looked
    /// through, because a machine in one root can be layered on a base in another.
    /// </summary>
    public static IReadOnlyList<string> PruneBases(IReadOnlyList<string> libraryRoots)
    {
        var used = UsedBases(libraryRoots, except: null);
        var deleted = new List<string>();
        foreach (var b in AllBases(libraryRoots))
        {
            if (used.Contains(b)) continue;
            File.Delete(b);
            deleted.Add(b);
        }

        return deleted;
    }

    /// <summary>The bases deleting the machine in <paramref name="directory"/> would leave unused (they go with it).</summary>
    public static IReadOnlyList<string> BasesFreedBy(string directory, IReadOnlyList<string> libraryRoots)
    {
        if (!Directory.Exists(directory)) return [];
        var mine = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).SelectMany(DiskState.Chain).ToHashSet(StringComparer.Ordinal);
        var others = UsedBases(libraryRoots, except: Path.GetFullPath(directory));
        return AllBases(libraryRoots).Where(b => mine.Contains(b) && !others.Contains(b)).ToList();
    }

    private static IEnumerable<string> AllBases(IReadOnlyList<string> libraryRoots) =>
        libraryRoots.Select(r => Path.Combine(r, BasesFolder)).Where(Directory.Exists).SelectMany(Directory.EnumerateFiles).Select(Path.GetFullPath).Distinct();

    /// <summary>Every file any machine's disk is layered on, through any number of layers.</summary>
    private static HashSet<string> UsedBases(IReadOnlyList<string> libraryRoots, string? except)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in libraryRoots.Where(Directory.Exists))
        {
            var bases = Path.GetFullPath(Path.Combine(root, BasesFolder));
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var full = Path.GetFullPath(dir);
                if (full == bases || full == except) continue;
                foreach (var f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                    foreach (var layer in DiskState.Chain(f).Skip(1)) used.Add(layer);
            }
        }

        return used;
    }

    private static async Task Overlay(string file, string baseFile, string baseFormat, CancellationToken ct) =>
        await QemuImg.RunAsync(ct, "create", "-q", "-f", "qcow2", "-o", "lazy_refcounts=on,nocow=on",
            "-b", Path.GetRelativePath(Path.GetDirectoryName(file)!, baseFile), "-F", baseFormat, file).ConfigureAwait(false);

    /// <summary>The files and folders deleting a machine removes, for the confirmation to list.</summary>
    public static IReadOnlyList<string> FilesToDelete(MachineConfig machine, string directory, string? confFile)
    {
        var list = new List<string> { directory };
        if (confFile is not null) list.Add(confFile);
        return list;
    }

    /// <summary>
    /// Deletes the machine's folder (and its quickemu .conf). Discs kept elsewhere are not touched. Refused while the
    /// machine runs (from anywhere: this app, another window, vmq), and for a folder that is not the machine's alone.
    /// </summary>
    public static void Delete(MachineConfig machine, string directory, string? confFile, IReadOnlyList<string> libraryRoots)
    {
        RefuseIfRunning(machine);
        if (DeleteRefusal(directory, confFile, libraryRoots) is { } why) throw new MachineBusyException(why);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        if (confFile is not null && File.Exists(confFile)) File.Delete(confFile);
    }

    /// <summary>
    /// Why a folder must not be deleted as a machine's own: it is a library folder or holds one, it is the home folder
    /// or the file system's root, or other machines live in it (a quickemu .conf can name a disk right beside the library
    /// folder, which would make the library folder "the machine's"). <c>null</c> = safe to delete.
    /// </summary>
    public static string? DeleteRefusal(string directory, string? confFile, IReadOnlyList<string> libraryRoots)
    {
        if (!Directory.Exists(directory)) return null;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var home = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        string Kept(string what) => $"VM Qontrol will not delete {full}: {what}. Delete the machine's own files by hand if you want them gone.";
        if (full.Length <= 1 || full == home) return Kept("it is not a machine's own folder");
        foreach (var root in libraryRoots.Where(r => r.Length > 0))
        {
            var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (r == full || r.StartsWith(full + "/", StringComparison.Ordinal)) return Kept("it is a library folder, holding other machines");
        }

        try
        {
            var others = Directory.EnumerateFiles(full, MachineStore.FileName, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true })
                .Any(f => Path.GetDirectoryName(f) != full);
            var confs = Directory.EnumerateFiles(full, "*.conf", SearchOption.TopDirectoryOnly)
                .Any(f => confFile is null || Path.GetFullPath(f) != Path.GetFullPath(confFile));
            if (others || confs) return Kept("other machines live in it");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Kept($"it could not be looked through ({ex.Message})");
        }

        return null;
    }

    /// <summary>Refuses work on a machine that runs, wherever it was started (the caller's own view may be out of date).</summary>
    public static void RefuseIfRunning(MachineConfig machine)
    {
        if (Runtime.MachineRunner.Find(machine, QontrolPaths.ForCurrentUser()) is not null)
            throw new MachineBusyException($"{machine.Name} is running (started from another window or vmq). Shut it down first.");
    }

    /// <summary>A locally administered MAC address in QEMU's 52:54:00 block.</summary>
    public static string NewMac()
    {
        var bytes = new byte[3];
        Random.Shared.NextBytes(bytes);
        return $"52:54:00:{bytes[0]:x2}:{bytes[1]:x2}:{bytes[2]:x2}";
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Work refused because the machine runs, or its folder is not its own; the message is for the person.</summary>
public sealed class MachineBusyException(string message) : IOException(message);
