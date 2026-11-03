using System.Diagnostics;
using System.Text.Json;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Core.Machines;

/// <summary>A machine's disk opened on this computer: where its files are, and whether changes are written back.</summary>
public sealed record OpenedDisk(string MachineId, string MachineName, int DiskIndex, string MountPoint, bool Writable);

/// <summary>Why a disk could not be opened, in words for the user; <see cref="KernelUnreadable"/> is the one a password fixes.</summary>
public sealed class DiskOpenException(string message, bool kernelUnreadable = false) : Exception(message)
{
    public bool KernelUnreadable { get; } = kernelUnreadable;
}

/// <summary>
/// Opens a machine's disk on this computer (D37), as Workstation's Map/Mount Disk: through libguestfs' guestmount, which
/// needs no root and reads the guest's filesystem (LVM, NTFS, btrfs and the rest) inside its own small appliance VM, so
/// this computer's kernel never parses a guest's disk. Read-only unless asked otherwise; never while the machine runs
/// (its disk is in use); a suspended machine read-only only (writing would break its resume). The disk opens in the
/// file manager, the machine will not start while it is open, and it stays open after VM Qontrol quits until closed.
/// </summary>
public static class DiskOpener
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static bool Available => Doctor.OnPath("guestmount") && Doctor.OnPath("guestunmount");

    /// <summary>Where opened disks appear: a folder per disk, named after the machine.</summary>
    public static string Root(QontrolPaths paths) => Path.Combine(paths.Runtime, "disks");

    private static string Record(QontrolPaths paths, string machineId, int disk) => Path.Combine(Root(paths), $".{machineId}-{disk}.json");

    /// <summary>The disks open now (a record whose folder is no longer mounted is cleared).</summary>
    public static IReadOnlyList<OpenedDisk> List(QontrolPaths paths)
    {
        var list = new List<OpenedDisk>();
        var root = Root(paths);
        if (!Directory.Exists(root)) return list;
        var mounted = MountPoints();
        foreach (var file in Directory.EnumerateFiles(root, ".*.json"))
        {
            try
            {
                var d = JsonSerializer.Deserialize<OpenedDisk>(File.ReadAllText(file));
                if (d is not null && mounted.Contains(d.MountPoint)) list.Add(d);
                else if (d is not null) Forget(file, d.MountPoint);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
            }
        }

        return list;
    }

    public static IReadOnlyList<OpenedDisk> OpenFor(QontrolPaths paths, string machineId) => [.. List(paths).Where(d => d.MachineId == machineId)];

    /// <summary>
    /// Opens disk <paramref name="diskIndex"/> of the machine and returns where; throws <see cref="DiskOpenException"/>
    /// with the reason in words.
    /// </summary>
    public static async Task<OpenedDisk> OpenAsync(MachineConfig m, string machineDirectory, QontrolPaths paths, int diskIndex, bool writable, CancellationToken ct = default)
    {
        if (!Available) throw new DiskOpenException($"Opening a disk needs libguestfs' guestmount. {Doctor.InstallCommand(Doctor.DetectThisDistro(), "guestmount")}");
        if (diskIndex < 0 || diskIndex >= m.Disks.Count) throw new DiskOpenException($"{m.Name} has no disk {diskIndex + 1}.");
        if (MachineRunner.Find(m, paths) is not null) throw new DiskOpenException($"{m.Name} is running, so its disk is in use. Shut it down first (or suspend it, to look without changing anything).");
        if (writable && MachineSuspend.IsSuspended(machineDirectory))
            throw new DiskOpenException($"{m.Name} is suspended: changing its disk would break its resume. Open it read-only, or discard the suspended state first.");
        if (OpenFor(paths, m.Id).FirstOrDefault(d => d.DiskIndex == diskIndex) is { } already) return already;

        var disk = m.Disks[diskIndex];
        var file = Path.Combine(machineDirectory, disk.File);
        if (!File.Exists(file)) throw new DiskOpenException($"The disk file {file} is missing.");
        var mountPoint = Path.Combine(Root(paths), m.Disks.Count == 1 ? Safe(m.Name) : $"{Safe(m.Name)} - disk {diskIndex + 1}");
        Directory.CreateDirectory(mountPoint);
        if (Directory.EnumerateFileSystemEntries(mountPoint).Any()) throw new DiskOpenException($"{mountPoint} is not empty; something else is there.");

        // The machine's system first (libguestfs finds it and mounts its filesystems where the guest does); a data disk
        // with no system on it, its first filesystem that mounts.
        var errors = new List<string>();
        foreach (var where in new[] { "-i", "/dev/sda1", "/dev/sda", "/dev/sda2", "/dev/sda3" })
        {
            var (ok, error) = await GuestmountAsync(file, disk.Format, where, mountPoint, writable, ct).ConfigureAwait(false);
            if (ok)
            {
                var opened = new OpenedDisk(m.Id, m.Name, diskIndex, mountPoint, writable);
                File.WriteAllText(Record(paths, m.Id, diskIndex), JsonSerializer.Serialize(opened, Json));
                Log.Info($"{m.Name}: disk {diskIndex + 1} opened {(writable ? "for writing" : "read-only")} at {mountPoint}");
                return opened;
            }

            errors.Add(error);
            if (Unreadable(error) is { } explained)
            {
                TryRemove(mountPoint);
                throw explained;
            }
        }

        TryRemove(mountPoint);
        var reason = errors.FirstOrDefault(e => e.Length > 0) ?? "guestmount found nothing it could mount";
        throw new DiskOpenException($"The disk could not be opened: {reason.TrimEnd('.')}. (An encrypted disk, such as BitLocker or LUKS, cannot be opened here.)");
    }

    /// <summary>Closes an opened disk: its changes are written out first (guestunmount waits for that).</summary>
    public static async Task CloseAsync(QontrolPaths paths, OpenedDisk opened, CancellationToken ct = default)
    {
        var start = new ProcessStartInfo("guestunmount") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        start.ArgumentList.Add("--retry=5");
        start.ArgumentList.Add(opened.MountPoint);
        Process p;
        try
        {
            p = Process.Start(start)!;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new DiskOpenException($"guestunmount (libguestfs) is not installed, so the disk cannot be closed from here: 'fusermount3 -u \"{opened.MountPoint}\"' closes it.");
        }

        using var _ = p;
        var error = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        // 2 = it was not mounted (already closed): fine.
        if (p.ExitCode is not (0 or 2))
            throw new DiskOpenException($"The disk is still in use on this computer (a file manager or terminal has a file open there): {error.Trim()}");
        Forget(Record(paths, opened.MachineId, opened.DiskIndex), opened.MountPoint);
        Log.Info($"{opened.MachineName}: disk {opened.DiskIndex + 1} closed");
    }

    private static async Task<(bool Ok, string Error)> GuestmountAsync(string file, string format, string where, string mountPoint, bool writable, CancellationToken ct)
    {
        var start = new ProcessStartInfo("guestmount") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        // libvirt is not VM Qontrol's: libguestfs starts its appliance with QEMU itself, as every machine here starts.
        start.Environment["LIBGUESTFS_BACKEND"] = "direct";
        string[] find = where == "-i" ? ["-i"] : ["-m", where];
        foreach (var a in (string[])[$"--format={format}", "-a", file, .. find, writable ? "--rw" : "--ro", mountPoint]) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        var error = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return (p.ExitCode == 0, Clean(error));
    }

    /// <summary>Ubuntu (and some Debian setups) keep the kernel readable by root only, and libguestfs builds its appliance from it.</summary>
    private static DiskOpenException? Unreadable(string error) =>
        error.Contains("supermin", StringComparison.Ordinal) && (error.Contains("vmlinuz", StringComparison.Ordinal) || error.Contains("kernel", StringComparison.OrdinalIgnoreCase))
            ? new DiskOpenException("libguestfs cannot read this computer's kernel (/boot/vmlinuz is readable by root only on Ubuntu), and it needs it to start. Allowing that takes your password once.", kernelUnreadable: true)
            : null;

    /// <summary>The script a password prompt runs so libguestfs can read the kernel, now and after kernel updates (libguestfs' own advice).</summary>
    public const string KernelScript =
        "chmod 0644 /boot/vmlinuz-* && if [ -d /etc/kernel/postinst.d ]; then " +
        "printf '#!/bin/sh\\nchmod 0644 /boot/vmlinuz-\"$1\" 2>/dev/null; true\\n' > /etc/kernel/postinst.d/zz-vmqontrol-guestfs && " +
        "chmod 0755 /etc/kernel/postinst.d/zz-vmqontrol-guestfs; fi";

    public static async Task<string?> AllowKernelAsync(CancellationToken ct = default)
    {
        if (!File.Exists("/usr/bin/pkexec")) return "polkit's pkexec is not installed, so the password cannot be asked for.";
        var start = new ProcessStartInfo("/usr/bin/pkexec") { RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in (string[])["/bin/sh", "-c", KernelScript]) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        var error = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return p.ExitCode switch { 0 => null, 126 => "The password prompt was closed.", 127 => "polkit refused.", _ => error.Trim() };
    }

    private static string Clean(string error) =>
        string.Join(' ', error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("libguestfs: trace", StringComparison.Ordinal))).Replace("guestmount: ", "", StringComparison.Ordinal).Trim();

    private static HashSet<string> MountPoints()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
            {
                var f = line.Split(' ');
                if (f.Length > 4) set.Add(f[4].Replace("\\040", " ", StringComparison.Ordinal).Replace("\\134", "\\", StringComparison.Ordinal));
            }
        }
        catch (IOException)
        {
        }

        return set;
    }

    private static void Forget(string record, string mountPoint)
    {
        try { File.Delete(record); } catch (IOException) { }
        TryRemove(mountPoint);
    }

    private static void TryRemove(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Safe(string name) => string.Concat(name.Select(c => c is '/' or '\0' ? '-' : c)).Trim();
}
