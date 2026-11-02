using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace VmQontrol.Core.Devices;

/// <summary>A serial port of this computer: a COM port on the board, or a USB serial adapter.</summary>
public sealed record HostSerialPort
{
    /// <summary>ttyS0, ttyUSB0, ttyACM0.</summary>
    public required string Name { get; init; }

    public required string DevicePath { get; init; }

    /// <summary>What a machine's settings keep: a /dev/serial/by-id link for an adapter (the same after a replug), else the device.</summary>
    public required string StablePath { get; init; }

    /// <summary>"COM1 (16550A)", or the adapter's own name.</summary>
    public required string Description { get; init; }

    public bool IsUsb { get; init; }

    /// <summary>This account may open it (it is in the group that owns serial ports, and that membership is in effect).</summary>
    public bool Accessible { get; init; }

    /// <summary>The group that owns the device node: uucp on Arch, dialout on Debian, Ubuntu and Fedora.</summary>
    public string? Group { get; init; }
}

/// <summary>
/// Lists this computer's serial ports from sysfs (COM ports the kernel found a UART behind, and USB serial adapters) and
/// gives this account access the way the distribution intends: membership of the group that owns serial ports, added
/// through polkit (pkexec asks for the password), in effect from the next sign-in. Nothing else changes on the system.
/// </summary>
public static partial class HostSerial
{
    private static readonly string[] UartTypes = ["unknown", "8250", "16450", "16550", "16550A", "Cirrus", "16650", "16650V2", "16750", "Startech", "16C950", "16654", "16850", "RSA"];

    public static IReadOnlyList<HostSerialPort> List(string sys = "/sys", string dev = "/dev")
    {
        var ports = new List<HostSerialPort>();
        var root = Path.Combine(sys, "class", "tty");
        if (!Directory.Exists(root)) return ports;
        var byId = StableNames(Path.Combine(dev, "serial", "by-id"));
        foreach (var dir in Directory.EnumerateFileSystemEntries(root).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            string description;
            var usb = name.StartsWith("ttyUSB", StringComparison.Ordinal) || name.StartsWith("ttyACM", StringComparison.Ordinal);
            if (name.StartsWith("ttyS", StringComparison.Ordinal))
            {
                // The kernel makes ttyS0-31 whether or not a UART is there; type 0 means nothing answered.
                if (!int.TryParse(Read(Path.Combine(dir, "type")), CultureInfo.InvariantCulture, out var type) || type == 0) continue;
                var com = int.TryParse(name[4..], CultureInfo.InvariantCulture, out var n) ? $"COM{n + 1}" : name;
                description = $"{com} ({(type < UartTypes.Length ? UartTypes[type] : "serial port")})";
            }
            else if (usb)
            {
                var props = Udev(Path.Combine("/run/udev/data", "c" + Read(Path.Combine(dir, "dev"))));
                var vendor = props.GetValueOrDefault("ID_VENDOR")?.Replace('_', ' ');
                var model = props.GetValueOrDefault("ID_MODEL")?.Replace('_', ' ') ?? "USB serial adapter";
                description = vendor is { Length: > 0 } && !model.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) ? $"{vendor} {model}" : model;
            }
            else
            {
                continue;
            }

            var path = Path.Combine(dev, name);
            ports.Add(new HostSerialPort
            {
                Name = name,
                DevicePath = path,
                StablePath = byId.GetValueOrDefault(name) ?? path,
                Description = description,
                IsUsb = usb,
                Accessible = File.Exists(path) && Access(path, 6 /* R_OK | W_OK */) == 0,
                Group = GroupOf(path),
            });
        }

        return ports;
    }

    /// <summary>The port a saved path names now, or <c>null</c> when it is not here.</summary>
    public static HostSerialPort? Find(string path, IReadOnlyList<HostSerialPort>? ports = null)
    {
        ports ??= List();
        string resolved;
        try
        {
            resolved = File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path;
        }
        catch (IOException)
        {
            return null;
        }

        return ports.FirstOrDefault(p => p.StablePath == path || p.DevicePath == path || p.DevicePath == resolved);
    }

    /// <summary>This account may open the device for reading and writing now.</summary>
    public static bool CanOpen(string path) => File.Exists(path) && Access(path, 6) == 0;

    /// <summary>Whether this account is listed in <paramref name="group"/> already (the sign-in it is in may predate that).</summary>
    public static bool Listed(string group, string user, string groupFile = "/etc/group")
    {
        try
        {
            return File.ReadLines(groupFile).Select(l => l.Split(':')).Any(f => f.Length >= 4 && f[0] == group && f[3].Split(',').Contains(user));
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Adds this account to <paramref name="group"/> through polkit: pkexec shows the desktop's password dialog. Returns
    /// <c>null</c> on success (in effect from the next sign-in), or why it did not happen.
    /// </summary>
    public static async Task<string?> JoinAsync(string group, CancellationToken ct = default)
    {
        var user = Environment.UserName;
        // gpasswd (shadow, every glibc distribution), usermod, and busybox's addgroup on Alpine.
        string[]? tool = File.Exists("/usr/bin/gpasswd") ? ["/usr/bin/gpasswd", "-a", user, group]
            : File.Exists("/usr/sbin/usermod") ? ["/usr/sbin/usermod", "-aG", group, user]
            : File.Exists("/usr/bin/usermod") ? ["/usr/bin/usermod", "-aG", group, user]
            : File.Exists("/usr/sbin/addgroup") ? ["/usr/sbin/addgroup", user, group]
            : null;
        if (tool is null) return "this computer has no tool to change group membership (gpasswd or usermod)";
        if (!File.Exists("/usr/bin/pkexec")) return "polkit's pkexec is not installed, so the password cannot be asked for";
        var start = new ProcessStartInfo("/usr/bin/pkexec") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in tool) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        var error = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return p.ExitCode switch
        {
            0 => null,
            126 => "the password prompt was closed",
            127 => "polkit refused (this account is not an administrator)",
            _ => error.Trim() is { Length: > 0 } e ? e : $"exit code {p.ExitCode}",
        };
    }

    private static Dictionary<string, string> StableNames(string byId)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(byId)) return map;
        foreach (var link in Directory.EnumerateFileSystemEntries(byId).Order(StringComparer.Ordinal))
        {
            try
            {
                if (File.ResolveLinkTarget(link, returnFinalTarget: true) is { } target) map.TryAdd(Path.GetFileName(target.FullName), link);
            }
            catch (IOException)
            {
            }
        }

        return map;
    }

    private static string? GroupOf(string path)
    {
        uint gid;
        try
        {
            if (Stat(path, out gid) != 0) return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null; // a libc without statx: the panel says "the serial group" instead of its name
        }

        try
        {
            return File.ReadLines("/etc/group").Select(l => l.Split(':')).FirstOrDefault(f => f.Length >= 3 && f[2] == gid.ToString(CultureInfo.InvariantCulture))?[0];
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>The owning group's id, through statx (the one stat call whose layout libc does not vary).</summary>
    private static unsafe int Stat(string path, out uint gid)
    {
        var buffer = stackalloc byte[256];
        var r = StatX(-100 /* AT_FDCWD */, path, 0, 0x8 /* STATX_GID */, (nint)buffer);
        gid = r == 0 ? *(uint*)(buffer + 24) : 0;
        return r;
    }

    private static Dictionary<string, string> Udev(string file)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                if (!line.StartsWith("E:", StringComparison.Ordinal)) continue;
                var eq = line.IndexOf('=', StringComparison.Ordinal);
                if (eq > 2) map[line[2..eq]] = line[(eq + 1)..];
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return map;
    }

    private static string? Read(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [LibraryImport("libc", EntryPoint = "access", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Access(string path, int mode);

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int StatX(int dirfd, string path, int flags, uint mask, nint buffer);
}
