using System.Diagnostics;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Core.Machines;

/// <summary>
/// Moves a machine's folder somewhere else (another library folder, another drive). On one filesystem the folder is
/// renamed; across filesystems it is copied (sparse, reflinked where the filesystem can) and the original removed only
/// once the copy is whole. A linked disk keeps its base: the relative name of the base is rewritten from the new place.
/// Refused while it runs, while suspended (the saved state names its files by full path), and for quickemu machines.
/// </summary>
public static class MachineMove
{
    /// <summary>Why the machine cannot move, or <c>null</c>.</summary>
    public static string? Refusal(string directory, string targetParent, bool running, bool quickemu)
    {
        if (running) return "Shut the machine down first: a machine moves only while it is off.";
        if (quickemu) return "A quickemu machine stays where quickemu keeps it; import it into the library to move it.";
        if (MachineSuspend.IsSuspended(directory)) return "The machine is suspended. Resume it and shut it down, or discard the suspended state, first.";
        var source = Path.GetFullPath(directory).TrimEnd('/');
        var parent = Path.GetFullPath(targetParent).TrimEnd('/');
        if (parent == Path.GetDirectoryName(source)) return "The machine is already in that folder.";
        if ((parent + "/").StartsWith(source + "/", StringComparison.Ordinal)) return "A machine cannot move into its own folder.";
        if (Directory.Exists(Path.Combine(parent, Path.GetFileName(source)))) return $"{Path.Combine(parent, Path.GetFileName(source))} already exists.";
        return null;
    }

    public static async Task<string> MoveAsync(MachineConfig machine, string directory, string targetParent, CancellationToken ct = default)
    {
        var source = Path.GetFullPath(directory).TrimEnd('/');
        var target = Path.Combine(Path.GetFullPath(targetParent), Path.GetFileName(source));
        // Bases outside the folder, read before anything moves: (disk name inside the folder, full path of its base).
        var links = machine.Disks
            .Select(d => Path.IsPathRooted(d.File) ? null : d.File)
            .OfType<string>()
            .Select(f => (File: f, Base: DiskState.BackingFile(Path.Combine(source, f))))
            .Where(l => l.Base is not null && !(l.Base + "/").StartsWith(source + "/", StringComparison.Ordinal))
            .ToList();

        Directory.CreateDirectory(Path.GetFullPath(targetParent));
        try
        {
            Directory.Move(source, target);
        }
        catch (IOException) when (!Directory.Exists(target) && Directory.Exists(source))
        {
            // Another filesystem: copy, check, then remove the original.
            await CopyTreeAsync(source, target, ct).ConfigureAwait(false);
            if (Size(source) != Size(target)) throw new IOException("The copy came out a different size; the machine was left where it was.");
            Directory.Delete(source, recursive: true);
        }

        foreach (var (file, baseFile) in links)
        {
            var disk = Path.Combine(target, file);
            var info = await QemuImg.InfoAsync(disk, ct).ConfigureAwait(false);
            await QemuImg.RunAsync(ct, "rebase", "-u", "-b", Path.GetRelativePath(Path.GetDirectoryName(disk)!, baseFile!),
                "-F", info["backing-filename-format"]?.GetValue<string>() ?? "qcow2", disk).ConfigureAwait(false);
        }

        return target;
    }

    private static async Task CopyTreeAsync(string source, string target, CancellationToken ct)
    {
        try
        {
            // GNU cp keeps disks sparse and reflinks on btrfs/XFS; busybox has neither option, so fall back.
            var start = new ProcessStartInfo("cp") { RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in (string[])["-a", "--sparse=always", "--reflink=auto", source, target]) start.ArgumentList.Add(a);
            using var p = Process.Start(start)!;
            var error = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            if (p.ExitCode == 0) return;
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            if (!error.Contains("unrecognized option", StringComparison.Ordinal) && !error.Contains("invalid option", StringComparison.Ordinal))
                throw new IOException($"The copy failed: {error.Trim()}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
        }
    }

    private static long Size(string dir) => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
}
