using System.Diagnostics;
using System.Globalization;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Core.Import;

/// <summary>Progress of an import: which disk, and how far through it.</summary>
public sealed record ImportProgress(int Disk, int Disks, double Percent, string Status);

/// <summary>
/// Brings a machine made elsewhere into the library (D30): reads it (<see cref="Read"/>), then copies each disk into a
/// new machine folder as qcow2 and writes vm.json (<see cref="ImportAsync"/>). The source is only read; a disk this
/// account cannot read (libvirt keeps its disks root-only) is read once through a polkit prompt.
/// </summary>
public static class MachineImporter
{
    /// <summary>The kinds of file an import starts from, for the file picker.</summary>
    public static readonly string[] Patterns = ["*.vmx", "*.vbox", "*.ovf", "*.ova", "*.xml", "*.qcow2", "*.vmdk", "*.vdi", "*.vhd", "*.vhdx", "*.img", "*.raw"];

    /// <summary>Reads any supported file. An .ova is unpacked into <paramref name="libraryRoot"/>/.importing first.</summary>
    public static ForeignMachine Read(string file, string libraryRoot)
    {
        var f = ReadAny(file, libraryRoot);
        // A machine without a disk is no machine to bring over (its disk may sit on a drive that is not connected).
        if (f.Disks.Count == 0 && f.Problems.Count == 0)
            f.Problems.Add($"No disk of this machine was found{(f.Kind == "disk" ? "" : $" in {Path.GetFileName(f.Source)}")}. If its disk lives on another drive, connect that drive and import again.");
        return f;
    }

    private static ForeignMachine ReadAny(string file, string libraryRoot)
    {
        file = Path.GetFullPath(file);
        var ext = Path.GetExtension(file).ToLowerInvariant();
        switch (ext)
        {
            case ".vmx":
                return VmxReader.Read(file);
            case ".vbox":
                return VboxReader.Read(file);
            case ".ovf":
                return OvfReader.Read(file);
            case ".ova":
            {
                var folder = Path.Combine(libraryRoot, ".importing", Guid.NewGuid().ToString("N")[..12]);
                try
                {
                    return OvfReader.Read(OvfReader.Unpack(file, folder), folder, file);
                }
                catch
                {
                    if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
                    throw;
                }
            }

            case ".xml":
                return LibvirtReader.Read(File.ReadAllText(file), file);
            case ".vmcx":
                return HyperV(file);
            default:
                return BareDisk(file);
        }
    }

    /// <summary>
    /// A Hyper-V export (Virtual Machines/ID.vmcx beside Virtual Hard Disks/). The .vmcx is a binary format only Hyper-V
    /// reads, so its disk is brought in and recognized from its partitions (a generation 2 machine is UEFI, generation 1
    /// BIOS, and the partition table shows which).
    /// </summary>
    public static ForeignMachine HyperV(string vmcx)
    {
        var export = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(vmcx))) ?? "";
        var disks = Path.Combine(export, "Virtual Hard Disks");
        var found = Directory.Exists(disks)
            ? Directory.EnumerateFiles(disks).Where(f => Path.GetExtension(f).ToLowerInvariant() is ".vhdx" or ".vhd" or ".avhdx").Where(f => !f.EndsWith(".avhdx", StringComparison.OrdinalIgnoreCase)).Order().ToList()
            : [];
        if (found.Count == 0)
            return new ForeignMachine
            {
                Kind = "hyper-v",
                Source = vmcx,
                Name = Path.GetFileName(export),
                Problems = [$"No disk was found in {disks}. Choose the machine's .vhdx disk itself instead."],
            };
        var machine = BareDisk(found[0]);
        machine.Notes.Insert(0, "Hyper-V keeps its settings in a format only Hyper-V reads, so the processors and memory are VM Qontrol's recommendations.");
        return machine with
        {
            Kind = "hyper-v",
            Source = vmcx,
            Name = Path.GetFileName(export) is { Length: > 0 } n ? n : machine.Name,
            Disks = [.. found.Select(d => new ForeignDisk(d, DiskBus.Auto))],
        };
    }

    /// <summary>
    /// A disk on its own: recognized from its partition table (DiskProbe): firmware and the kind of system; Windows 11
    /// when it starts with UEFI (it needs it), Windows 10 otherwise. The person can change both on the review page.
    /// </summary>
    public static ForeignMachine BareDisk(string disk)
    {
        var facts = DiskProbe.Probe(disk);
        var guest = facts?.Os switch
        {
            "windows" => new GuestConfig { Os = "windows", Release = facts.Uefi == false ? "10" : "11" },
            "bsd" => new GuestConfig { Os = "freebsd" },
            _ => new GuestConfig { Os = "linux" },
        };
        var firmware = facts?.Uefi == false ? FirmwareKind.Bios : FirmwareKind.Uefi;
        var notes = new List<string>();
        if (facts is null)
            notes.Add("The disk could not be read to recognize it: check the system and the firmware at the top. A disk from an older BIOS machine needs BIOS firmware to start.");
        else if (facts.Os is "macos")
            notes.Add($"This looks like a macOS disk ({facts.Evidence}). macOS machines are made by the New machine wizard's download; this disk may not start here.");
        else
            notes.Add($"Recognized from the disk: {facts.Evidence}, so {(facts.Os switch { "windows" => "Windows", "linux" => "Linux", "bsd" => "a BSD", _ => "an unknown system" })}"
                + $"{(facts.Uefi is { } u ? $" starting with {(u ? "UEFI" : "BIOS")}" : "")}. Change the system or the firmware at the top if that is wrong.");
        return new ForeignMachine
        {
            Kind = "disk",
            Source = disk,
            Name = Path.GetFileNameWithoutExtension(disk),
            Guest = guest,
            Firmware = firmware,
            Tpm = guest is { Os: "windows", Release: "11" },
            SecureBoot = false,
            Disks = [new ForeignDisk(disk, DiskBus.Auto)],
            Nics = [new NicConfig()],
            Notes = notes,
        };
    }

    /// <summary>Copies the machine into a new folder in <paramref name="libraryRoot"/>. Call on a worker.</summary>
    public static async Task<(MachineConfig Machine, string Directory)> ImportAsync(ForeignMachine f, string libraryRoot, IProgress<ImportProgress> progress, CancellationToken ct)
    {
        if (f.Problems.Count > 0) throw new InvalidOperationException(string.Join(" ", f.Problems));
        // The descriptor is someone else's file: what it names must be a disk or disc image, nowhere in the system's folders.
        var refused = f.Disks.Select(d => ForeignPaths.Problem(d.Path)).Concat(f.Discs.Select(d => ForeignPaths.Problem(d, disc: true))).OfType<string>().ToList();
        if (refused.Count > 0) throw new InvalidOperationException(string.Join(" ", refused));
        var dir = MachineFactory.FolderFor(libraryRoot, f.Name);
        Directory.CreateDirectory(dir);
        try
        {
            var disks = new List<DiskConfig>();
            for (var i = 0; i < f.Disks.Count; i++)
            {
                var source = f.Disks[i];
                var target = Path.Combine(dir, $"disk{i}.qcow2");
                var index = i;
                await ConvertAsync(source.Path, target, new Progress<double>(p =>
                    progress.Report(new ImportProgress(index + 1, f.Disks.Count, p, $"Copying disk {index + 1} of {f.Disks.Count}…"))), ct,
                    mayElevate: ForeignPaths.MayElevate(f, source.Path)).ConfigureAwait(false);
                var info = await QemuImg.InfoAsync(target, ct).ConfigureAwait(false);
                var bytes = info["virtual-size"]?.GetValue<long>() ?? 0;
                disks.Add(new DiskConfig { File = Path.GetFileName(target), Bus = source.Bus, SizeGiB = Math.Max(1, (bytes + (1L << 30) - 1) >> 30) });
            }

            var windows = f.Guest.Os is "windows" or "windows-server";
            var machine = new MachineConfig
            {
                Name = f.Name,
                Guest = f.Guest,
                // Someone else's numbers: kept only when a machine could run with them (else the recommended value).
                MemoryMiB = f.MemoryMiB is >= 64 and <= 4L << 20 ? f.MemoryMiB : null,
                Cpu = new CpuConfig { Cores = f.Cores is >= 1 and <= 1024 ? f.Cores : null },
                Firmware = new FirmwareConfig { Kind = f.Firmware, SecureBoot = f.SecureBoot },
                // Windows 11 refuses to start without a TPM it had; a TPM is harmless elsewhere.
                Tpm = f.Tpm || (windows && f.Guest.Release == "11"),
                Disks = disks,
                Nics = f.Nics.Count > 0 ? f.Nics : [new NicConfig()],
                Media = f.Discs.Take(1).Select(d => new MediaConfig { Role = MediaRole.Drivers, File = d, Connect = Connect.Always }).ToList(),
                Notes = $"Imported from {f.Kind} ({f.Source}) on {DateTime.Now:d MMMM yyyy}." + (f.Notes.Count > 0 ? "\n" + string.Join("\n", f.Notes.Select(n => "• " + n)) : ""),
                CopiedFrom = new ImportSource(f.Kind, f.Source),
            };
            MachineStore.Save(machine, Path.Combine(dir, MachineStore.FileName));
            Log.Info($"Imported {f.Kind} machine {f.Source} into {dir} ({disks.Count} disk{(disks.Count == 1 ? "" : "s")}).");
            return (machine, dir);
        }
        catch
        {
            // A half-made machine is no machine: nothing of this attempt stays.
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            throw;
        }
        finally
        {
            if (f.TemporaryFolder is { } tmp && Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        }
    }

    /// <summary>
    /// qemu-img convert to qcow2 with its progress (qemu-img follows VMware snapshot chains through the descriptors).
    /// A source this account cannot read is converted through pkexec, and the copy handed back to this account.
    /// </summary>
    public static async Task ConvertAsync(string source, string target, IProgress<double> progress, CancellationToken ct, bool mayElevate = false)
    {
        string[] convert = ["convert", "-p", "-O", "qcow2", "-o", "lazy_refcounts=on,nocow=on", source, target];
        ProcessStartInfo start;
        if (!CanRead(source) && !mayElevate)
            throw new IOException($"{source} cannot be read by this account. Copy it somewhere this account can read (or give it read access), then import again.");
        if (CanRead(source))
        {
            start = new ProcessStartInfo(QemuImg.Program);
            foreach (var a in convert) start.ArgumentList.Add(a);
        }
        else
        {
            // $0 = qemu-img, $1..$8 = the convert arguments; $8 is the copy, handed to the person who asked.
            start = new ProcessStartInfo("pkexec");
            foreach (var a in new[] { "sh", "-c", "\"$0\" \"$@\" && chown \"$PKEXEC_UID\" \"$8\"", "qemu-img" }.Concat(convert)) start.ArgumentList.Add(a);
        }

        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new IOException("qemu-img could not be started.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new IOException($"{start.FileName} is not installed, so the disk could not be copied. vmq doctor (or the welcome page) names the package.");
        }

        using var owned = process;
        using var kill = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        // "-p" writes "    (12.34/100%)\r" as it goes.
        var buffer = new char[256];
        var line = new System.Text.StringBuilder();
        int read;
        while ((read = await process.StandardOutput.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) > 0)
        {
            foreach (var c in buffer.AsSpan(0, read))
            {
                if (c is '\r' or '\n')
                {
                    var text = line.ToString().Trim().Trim('(', ')');
                    if (text.Split('/')[0] is var pct && double.TryParse(pct, NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) progress.Report(p);
                    line.Clear();
                }
                else line.Append(c);
            }
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            var why = (await error.ConfigureAwait(false)).Trim();
            throw new IOException(process.ExitCode == 126 && start.FileName == "pkexec"
                ? $"Reading {source} needs permission, and the password prompt was dismissed."
                : $"The disk {Path.GetFileName(source)} could not be copied: {why}");
        }
    }

    private static bool CanRead(string file)
    {
        try
        {
            using var _ = File.OpenRead(file);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return true; // missing or unreadable for another reason: qemu-img says which, in its own words
        }
    }
}
