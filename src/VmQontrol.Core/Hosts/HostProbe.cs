using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace VmQontrol.Core.Hosts;

/// <summary>Reads <see cref="HostCapabilities"/> from the running system. Slow (it runs QEMU twice): call it off the UI thread.</summary>
public static partial class HostProbe
{
    public static HostCapabilities Probe(string qemu = "qemu-system-x86_64")
    {
        var cpuinfo = ReadOr("/proc/cpuinfo");
        var vendor = FirstField(cpuinfo, "vendor_id") ?? "";
        var flags = (FirstField(cpuinfo, "flags") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var sockets = Math.Max(1, cpuinfo.Split('\n').Where(l => l.StartsWith("physical id", StringComparison.Ordinal)).Distinct().Count());
        var smt = ReadOr("/sys/devices/system/cpu/smt/control").Trim() == "on";
        var nestedModule = vendor == "AuthenticAMD" ? "kvm_amd" : vendor == "GenuineIntel" ? "kvm_intel" : null;

        return new HostCapabilities
        {
            CpuVendor = vendor,
            LogicalCpus = Environment.ProcessorCount,
            SmtOn = smt,
            Sockets = sockets,
            MemoryGB = MemoryGigabytes(ReadOr("/proc/meminfo")),
            CpuFlags = flags,
            QemuVersion = ParseQemuVersion(Run(qemu, "--version")),
            QemuDevices = ParseDeviceNames(Run(qemu, "-device", "help")),
            KvmUsable = CanOpenReadWrite("/dev/kvm"),
            NestedKvm = NestedVirt.Read(nestedModule),
            RenderNode = File.Exists("/dev/dri/renderD128") ? "/dev/dri/renderD128" : null,
            FirmwareFiles = FirmwareLocator.Candidates().Where(File.Exists).ToHashSet(StringComparer.Ordinal),
        };
    }

    /// <summary>MemTotal in decimal gigabytes, truncated as <c>free --giga</c> does (63389236 kB → 64, not 65).</summary>
    public static int MemoryGigabytes(string meminfo)
    {
        var match = MemTotal().Match(meminfo);
        if (!match.Success) return 0;
        var bytes = long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 1024;
        return (int)(bytes / 1_000_000_000);
    }

    public static Version ParseQemuVersion(string output)
    {
        var match = QemuVersionLine().Match(output);
        return match.Success ? Version.Parse(match.Groups[1].Value) : new Version(0, 0, 0);
    }

    public static HashSet<string> ParseDeviceNames(string output) =>
        DeviceName().Matches(output).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    private static string? FirstField(string text, string name)
    {
        foreach (var line in text.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim() == name) return line[(colon + 1)..].Trim();
        }

        return null;
    }

    private static string ReadOr(string file)
    {
        try
        {
            return File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static bool CanOpenReadWrite(string device)
    {
        try
        {
            using var _ = new FileStream(device, FileMode.Open, FileAccess.ReadWrite);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Run(string program, params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) start.ArgumentList.Add(a);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10_000)) process.Kill(entireProcessTree: true);
            return output.Result + error.Result;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "";
        }
    }

    [GeneratedRegex(@"^MemTotal:\s+(\d+)\s+kB", RegexOptions.Multiline)]
    private static partial Regex MemTotal();

    [GeneratedRegex(@"version (\d+\.\d+\.\d+)")]
    private static partial Regex QemuVersionLine();

    // Names and aliases ("virtio-gpu-gl" is only an alias of virtio-gpu-gl-pci); quickemu's text search finds both.
    [GeneratedRegex("(?:^name|alias) \"([^\"]+)\"", RegexOptions.Multiline)]
    private static partial Regex DeviceName();
}
