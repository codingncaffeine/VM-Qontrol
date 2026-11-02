using System.Text.Json;

namespace VmQontrol.Core.Machines;

/// <summary>One saved change to a machine's settings: when, what changed in words, and the settings before it.</summary>
public sealed record SettingsChange(DateTimeOffset At, IReadOnlyList<string> Changes, string Before);

/// <summary>
/// Every save of a machine's settings is kept (settings-history.jsonl beside its vm.json, or in VM Qontrol's own state
/// for an imported machine), so any change can be read back and undone. Neither VMware Workstation nor virt-manager
/// keeps one.
/// </summary>
public static class SettingsHistory
{
    public const string FileName = "settings-history.jsonl";
    private const int Keep = 200;

    /// <summary>The changes between two versions of a machine, in words ("Memory: 8 GB → 16 GB").</summary>
    public static IReadOnlyList<string> Describe(MachineConfig before, MachineConfig after)
    {
        var changes = new List<string>();
        void Diff(string what, object? a, object? b, Func<object?, string>? say = null)
        {
            say ??= v => v?.ToString() ?? "recommended";
            if (!Equals(a, b)) changes.Add($"{what}: {say(a)} → {say(b)}");
        }

        string Mib(object? v) => v is long m ? (m % 1024 == 0 ? $"{m / 1024} GB" : $"{m} MB") : "recommended";
        Diff("Name", before.Name, after.Name);
        Diff("Notes", before.Notes.Length, after.Notes.Length, _ => "edited");
        Diff("Template", before.IsTemplate, after.IsTemplate, v => (bool?)v == true ? "yes" : "no");
        Diff("Memory", before.MemoryMiB, after.MemoryMiB, Mib);
        Diff("Processors", before.Cpu.Cores, after.Cpu.Cores);
        Diff("Processor model", before.Cpu.Model, after.Cpu.Model, v => v?.ToString() ?? "automatic");
        Diff("Firmware", before.Firmware.Kind, after.Firmware.Kind);
        Diff("Secure Boot", before.Firmware.SecureBoot, after.Firmware.SecureBoot, v => (bool?)v == true ? "on" : "off");
        Diff("TPM", before.Tpm, after.Tpm, v => (bool?)v == true ? "on" : "off");
        Diff("Disks", string.Join(", ", before.Disks.Select(d => $"{Path.GetFileName(d.File)} {d.SizeGiB} GB {d.Bus}")), string.Join(", ", after.Disks.Select(d => $"{Path.GetFileName(d.File)} {d.SizeGiB} GB {d.Bus}")));
        Diff("CD/DVD and floppies", string.Join(", ", before.Media.Select(m => $"{m.Role} {Path.GetFileName(m.File)} {m.Connect}")), string.Join(", ", after.Media.Select(m => $"{m.Role} {Path.GetFileName(m.File)} {m.Connect}")));
        Diff("Network", string.Join(", ", before.Nics.Select(Nic)), string.Join(", ", after.Nics.Select(Nic)));
        Diff("Display", $"{before.Display.Backend} {before.Display.Device} GL {before.Display.Gl}", $"{after.Display.Backend} {after.Display.Device} GL {after.Display.Gl}");
        Diff("Starting resolution", (before.Display.Width, before.Display.Height), (after.Display.Width, after.Display.Height),
            v => v is ValueTuple<int?, int?> { Item1: { } w, Item2: { } h } ? $"{w} × {h}" : "automatic");
        Diff("Viewer program", before.Display.Viewer, after.Display.Viewer);
        Diff("Full screen", before.Display.FullScreen, after.Display.FullScreen, v => (bool?)v == true ? "on" : "off");
        Diff("Sound", before.Audio.Device, after.Audio.Device, v => v?.ToString() ?? "automatic");
        Diff("Keyboard", before.Input.Keyboard, after.Input.Keyboard, v => v?.ToString() ?? "automatic");
        Diff("Pointer", before.Input.Mouse, after.Input.Mouse, v => v?.ToString() ?? "automatic");
        Diff("Keyboard layout", before.Input.Layout, after.Input.Layout);
        Diff("USB controller", before.Usb.Controller, after.Usb.Controller, v => v?.ToString() ?? "automatic");
        Diff("USB devices", string.Join(", ", before.Usb.Passthrough.Select(u => u.Name ?? $"{u.VendorId}:{u.ProductId}")), string.Join(", ", after.Usb.Passthrough.Select(u => u.Name ?? $"{u.VendorId}:{u.ProductId}")));
        Diff("Serial port", before.Serial, after.Serial, v => v is bool b ? (b ? "on" : "off") : "automatic");
        Diff("Random-number generator", before.Devices.Rng ?? true, after.Devices.Rng ?? true, v => (bool?)v == true ? "on" : "off");
        Diff("Memory ballooning", before.Devices.Balloon, after.Devices.Balloon, v => v is bool b ? (b ? "on" : "off") : "automatic");
        Diff("Watchdog", before.Devices.Watchdog, after.Devices.Watchdog, v => v?.ToString() ?? "none");
        Diff("Crash reporting", before.Devices.CrashReporting, after.Devices.CrashReporting, v => (bool?)v == true ? "on" : "off");
        Diff("Boot order", before.Devices.BootOrder, after.Devices.BootOrder, v => v?.ToString() ?? "automatic");
        Diff("Boot menu", before.Devices.BootMenu, after.Devices.BootMenu, v => (bool?)v == true ? "on" : "off");
        Diff("Shared folders", string.Join(", ", before.Shares.Select(x => $"{x.Tag}={x.Path}{(x.ReadOnly ? " (read only)" : "")}")), string.Join(", ", after.Shares.Select(x => $"{x.Tag}={x.Path}{(x.ReadOnly ? " (read only)" : "")}")), v => (v as string) is { Length: > 0 } t ? t : "none");
        Diff("AutoProtect", AutoProtectText(before.AutoProtect), AutoProtectText(after.AutoProtect));
        Diff("Extra QEMU arguments", string.Join(' ', before.ExtraArgs), string.Join(' ', after.ExtraArgs), v => (v as string) is { Length: > 0 } s ? s : "none");
        return changes;
    }

    private static string AutoProtectText(AutoProtectConfig a) => a.Enabled ? $"every {a.IntervalMinutes} min, keep {a.Keep}" : "off";

    private static string Nic(NicConfig n) => $"{n.Mode}{(n.Bridge is { } b ? " " + b : "")}{(n.Model is { } m ? " " + m : "")}{(n.Mac is { } mac ? " " + mac : "")}" +
                                              (n.Forwards.Count > 0 ? $" forwards {string.Join(' ', n.Forwards.Select(f => $"{f.Protocol}:{f.HostPort}->{f.GuestPort}"))}" : "");

    public static string PathFor(string historyDirectory) => Path.Combine(historyDirectory, FileName);

    /// <summary>Saves <paramref name="after"/> as the machine's settings and records the change; returns what changed.</summary>
    public static IReadOnlyList<string> Save(MachineConfig before, MachineConfig after, string vmJson, string historyDirectory)
    {
        var changes = Describe(before, after);
        if (changes.Count == 0) return changes;
        MachineStore.Save(after, vmJson);
        Directory.CreateDirectory(historyDirectory);
        var line = JsonSerializer.Serialize(new SettingsChange(DateTimeOffset.Now, changes, MachineStore.ToJson(before)), HistoryJson.Options);
        var file = PathFor(historyDirectory);
        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : [];
        lines.Add(line);
        File.WriteAllLines(file, lines.TakeLast(Keep));
        return changes;
    }

    /// <summary>The recorded changes, newest first.</summary>
    public static IReadOnlyList<SettingsChange> Read(string historyDirectory)
    {
        var file = PathFor(historyDirectory);
        if (!File.Exists(file)) return [];
        return File.ReadAllLines(file).Select(l =>
        {
            try
            {
                return JsonSerializer.Deserialize<SettingsChange>(l, HistoryJson.Options);
            }
            catch (JsonException)
            {
                return null;
            }
        }).Where(c => c is not null).Select(c => c!).Reverse().ToList();
    }

    /// <summary>The settings as they were before a recorded change.</summary>
    public static MachineConfig Before(SettingsChange change) =>
        JsonSerializer.Deserialize(change.Before, MachineJsonContext.Default.MachineConfig) ?? throw new InvalidDataException("The recorded settings could not be read.");

    private static class HistoryJson
    {
        public static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    }
}
