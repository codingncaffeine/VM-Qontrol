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
                ImportedFrom = null,
                Disks = disks,
                Media = source.Media.Select(m => m with { File = Full(m.File) }).ToList(),
                Firmware = source.Firmware with { VarsFile = "OVMF_VARS.fd", CodeFile = source.Firmware.CodeFile is { } c ? Full(c) : null },
                KernelBoot = source.KernelBoot is { } kb ? kb with { Kernel = Full(kb.Kernel), Initrd = kb.Initrd is { } initrd ? Full(initrd) : null } : null,
                Nics = nic is null ? source.Nics : [nic with { Mac = nic.Mac is null ? null : NewMac(), SshHostPort = null }, .. source.Nics.Skip(1)],
                Notes = source.Notes.Length > 0 ? source.Notes : $"Cloned from {source.Name} on {DateTime.Now:d}.",
            };
            MachineStore.Save(clone, Path.Combine(target, MachineStore.FileName));
            return (clone, target);
        }
        catch
        {
            TryDelete(target);
            throw;
        }
    }

    /// <summary>The files and folders deleting a machine removes, for the confirmation to list.</summary>
    public static IReadOnlyList<string> FilesToDelete(MachineConfig machine, string directory, string? confFile)
    {
        var list = new List<string> { directory };
        if (confFile is not null) list.Add(confFile);
        return list;
    }

    /// <summary>Deletes the machine's folder (and its quickemu .conf). Discs kept elsewhere are not touched.</summary>
    public static void Delete(string directory, string? confFile)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        if (confFile is not null && File.Exists(confFile)) File.Delete(confFile);
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
