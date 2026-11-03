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
            default:
                return BareDisk(file);
        }
    }

    /// <summary>A disk on its own: the system is the person's to choose; UEFI unless they say otherwise.</summary>
    public static ForeignMachine BareDisk(string disk) => new()
    {
        Kind = "disk",
        Source = disk,
        Name = Path.GetFileNameWithoutExtension(disk),
        Guest = new GuestConfig { Os = "linux" },
        Firmware = FirmwareKind.Uefi,
        Disks = [new ForeignDisk(disk, DiskBus.Auto)],
        Nics = [new NicConfig()],
        Notes = ["A disk on its own says nothing about the machine it came from: check the system and the firmware below. A disk from an older BIOS machine needs BIOS firmware to start."],
    };

    /// <summary>Copies the machine into a new folder in <paramref name="libraryRoot"/>. Call on a worker.</summary>
    public static async Task<(MachineConfig Machine, string Directory)> ImportAsync(ForeignMachine f, string libraryRoot, IProgress<ImportProgress> progress, CancellationToken ct)
    {
        if (f.Problems.Count > 0) throw new InvalidOperationException(string.Join(" ", f.Problems));
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
                    progress.Report(new ImportProgress(index + 1, f.Disks.Count, p, $"Copying disk {index + 1} of {f.Disks.Count}…"))), ct).ConfigureAwait(false);
                var info = await QemuImg.InfoAsync(target, ct).ConfigureAwait(false);
                var bytes = info["virtual-size"]?.GetValue<long>() ?? 0;
                disks.Add(new DiskConfig { File = Path.GetFileName(target), Bus = source.Bus, SizeGiB = Math.Max(1, (bytes + (1L << 30) - 1) >> 30) });
            }

            var windows = f.Guest.Os is "windows" or "windows-server";
            var machine = new MachineConfig
            {
                Name = f.Name,
                Guest = f.Guest,
                MemoryMiB = f.MemoryMiB,
                Cpu = new CpuConfig { Cores = f.Cores },
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
    public static async Task ConvertAsync(string source, string target, IProgress<double> progress, CancellationToken ct)
    {
        string[] convert = ["convert", "-p", "-O", "qcow2", "-o", "lazy_refcounts=on,nocow=on", source, target];
        ProcessStartInfo start;
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
        using var process = Process.Start(start) ?? throw new IOException("qemu-img could not be started.");
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
