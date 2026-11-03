using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using VmQontrol.Core.Library;
using VmQontrol.Core.Machines;

namespace VmQontrol.Core.Hosts;

/// <summary>
/// "Collect support data" (Workstation's, VirtualBox's log export): one .tar.gz with what a person helping needs — this
/// computer's system, QEMU and KVM, the doctor's checks, VM Qontrol's own logs, each machine's settings and logs — and
/// nothing personal: the home folder reads "~", the user name "&lt;user&gt;", notes and Easy Install account names are
/// left out, and no disk, firmware state, answer file or screenshot is ever included.
/// </summary>
public static partial class SupportBundle
{
    /// <summary>Most of each log kept (its end, where the trouble is).</summary>
    private const int LogTail = 2 * 1024 * 1024;

    /// <summary>Writes the bundle to <paramref name="file"/> and returns the names of what went in.</summary>
    public static IReadOnlyList<string> Create(string file, QontrolPaths paths, HostCapabilities host, IReadOnlyList<DoctorCheck> checks)
    {
        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["system.txt"] = SystemText(host),
            ["doctor.txt"] = string.Join('\n', checks.Select(c => $"{c.Status,-9} {c.Title}: {c.Detail}{(c.Fix is { } f && c.Status != CheckStatus.Ok ? "\n          " + f : "")}")) + "\n",
        };
        foreach (var log in Files(paths.Logs, "*.log*")) entries[$"logs/{Path.GetFileName(log)}"] = Tail(log);

        var settings = AppSettings.Load(paths);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in settings.LibraryRoots)
        {
            foreach (var entry in LibraryScanner.Scan(root, paths.State))
            {
                if (entry.Machine is not { } m) continue;
                var name = SafeName(m.Name);
                while (!names.Add(name)) name += "-" + m.Id[..4];
                entries[$"machines/{name}/vm.json"] = MachineStore.ToJson(m with
                {
                    Notes = "",
                    Guest = m.Guest with { Account = m.Guest.Account is null ? null : "<account>" },
                    EasyInstall = m.EasyInstall is { } e ? e with { UserName = e.UserName.Length > 0 ? "<account>" : "" } : null,
                });
                foreach (var log in Files(Runtime.MachineRunner.LogDirectory(paths, m), "*.log*"))
                    entries[$"machines/{name}/logs/{Path.GetFileName(log)}"] = Tail(log);
            }
        }

        var partial = file + ".part";
        using (var output = File.Create(partial))
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax))
        {
            foreach (var (name, text) in entries)
            {
                var bytes = Encoding.UTF8.GetBytes(Redact(text));
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"vmqontrol-support/{name}")
                {
                    DataStream = new MemoryStream(bytes),
                    Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
                    ModificationTime = DateTimeOffset.UtcNow,
                });
            }
        }

        File.Move(partial, file, overwrite: true);
        return [.. entries.Keys];
    }

    /// <summary>The home folder as "~" and the user's name as "&lt;user&gt;", wherever they appear (paths, log lines).</summary>
    public static string Redact(string text)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 1) text = text.Replace(home, "~", StringComparison.Ordinal);
        var user = Environment.UserName;
        // A very short name would match inside ordinary words; such names are left to the path rule above.
        if (user.Length >= 3) text = Regex.Replace(text, $@"(?<![A-Za-z0-9]){Regex.Escape(user)}(?![A-Za-z0-9])", "<user>");
        return text;
    }

    private static string SystemText(HostCapabilities host)
    {
        static string Read(string path) { try { return File.ReadAllText(path).Trim(); } catch (IOException) { return "?"; } catch (UnauthorizedAccessException) { return "?"; } }
        var model = Read("/proc/cpuinfo").Split('\n').FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim() ?? "?";
        return $"""
            VM Qontrol {BuildInfo.Stamp}
            System: {Doctor.ThisSystemName() ?? "?"}
            Kernel: {Read("/proc/sys/kernel/osrelease")}
            Processor: {model} ({host.CpuVendor}, {host.LogicalCpus} threads, {host.Sockets} socket{(host.Sockets == 1 ? "" : "s")}, SMT {(host.SmtOn ? "on" : "off")})
            Memory: {host.MemoryGB} GB
            QEMU: {host.QemuVersion}
            KVM usable: {(host.KvmUsable ? "yes" : "no")}, nested: {host.NestedKvm?.ToString() ?? "unknown"}
            Render node: {host.RenderNode ?? "none"}
            Session: {Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") ?? "?"} on {Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "?"}

            """;
    }

    private static IEnumerable<string> Files(string dir, string pattern) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern).Order(StringComparer.Ordinal) : [];

    private static string Tail(string path)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (f.Length > LogTail) f.Seek(-LogTail, SeekOrigin.End);
            using var reader = new StreamReader(f, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"(could not be read: {ex.Message})\n";
        }
    }

    private static string SafeName(string name) => UnsafeName().Replace(name, "_") is { Length: > 0 } s ? s : "machine";

    [GeneratedRegex(@"[^A-Za-z0-9._ -]")]
    private static partial Regex UnsafeName();
}
