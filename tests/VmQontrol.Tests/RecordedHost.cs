using VmQontrol.Core.Hosts;

namespace VmQontrol.Tests;

/// <summary>The host facts tools/record-oracle.sh saved beside quickemu's command lines (Fixtures/quickemu/host.txt).</summary>
internal static class RecordedHost
{
    public static string FixtureDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures", "quickemu");

    public static HostCapabilities Load()
    {
        var facts = File.ReadAllLines(Path.Combine(FixtureDirectory, "host.txt"))
            .Select(l => l.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1].Trim());
        HashSet<string> Words(string key) => facts[key].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return new HostCapabilities
        {
            CpuVendor = facts["vendor"],
            LogicalCpus = int.Parse(facts["logical"], System.Globalization.CultureInfo.InvariantCulture),
            SmtOn = facts["smt"] == "on",
            Sockets = int.Parse(facts["sockets"], System.Globalization.CultureInfo.InvariantCulture),
            MemoryGB = int.Parse(facts["memory_gb"], System.Globalization.CultureInfo.InvariantCulture),
            QemuVersion = Version.Parse(facts["qemu"]),
            CpuFlags = Words("flags"),
            QemuDevices = Words("devices"),
            KvmUsable = true,
            RenderNode = facts["rendernode"].Length > 0 ? facts["rendernode"] : null,
            FirmwareFiles = Words("firmware"),
        };
    }
}
