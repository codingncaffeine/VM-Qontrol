using System.Globalization;
using VmQontrol.Core.Machines;

namespace VmQontrol.Core.Import;

/// <summary>Reads a VMware Workstation / Fusion / Player machine (.vmx: key = "value" lines).</summary>
public static class VmxReader
{
    public static Dictionary<string, string> Parse(string text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) continue;
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            map[line[..eq].Trim()] = value.Replace("|22", "\"", StringComparison.Ordinal).Replace("|7C", "|", StringComparison.Ordinal);
        }

        return map;
    }

    public static ForeignMachine Read(string vmx)
    {
        vmx = Path.GetFullPath(vmx);
        var dir = Path.GetDirectoryName(vmx)!;
        var v = Parse(File.ReadAllText(vmx));
        string? Get(string key) => v.TryGetValue(key, out var x) ? x : null;
        bool On(string key) => Get(key) is { } x && x.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
        string Resolve(string f) => Path.GetFullPath(Path.IsPathRooted(f) ? f : Path.Combine(dir, f));
        var notes = new List<string>();

        var guestOs = Get("guestOS") ?? "other";
        var disks = new List<ForeignDisk>();
        var discs = new List<string>();
        // Controllers in the order VMware boots them by default; each device is "<ctl><n>:<unit>".
        foreach (var (prefix, bus) in new[] { ("nvme", DiskBus.Nvme), ("sata", DiskBus.Sata), ("scsi", DiskBus.Auto), ("ide", DiskBus.Sata) })
        {
            for (var c = 0; c < 4; c++)
            {
                if (!On($"{prefix}{c}.present")) continue;
                var controllerBus = bus;
                if (prefix == "scsi")
                {
                    var kind = Get($"scsi{c}.virtualDev") ?? "lsilogic";
                    controllerBus = kind.Equals("pvscsi", StringComparison.OrdinalIgnoreCase) ? DiskBus.Pvscsi : DiskBus.Sata;
                    if (controllerBus == DiskBus.Sata)
                        notes.Add($"The disk on VMware's {kind} SCSI controller is connected as SATA here (QEMU has no twin the system would have a driver for). If it does not boot, choose another disk type in Settings.");
                }

                for (var u = 0; u < 64; u++)
                {
                    var key = $"{prefix}{c}:{u}";
                    if (!On($"{key}.present") || Get($"{key}.fileName") is not { Length: > 0 } file) continue;
                    var type = Get($"{key}.deviceType") ?? "disk";
                    if (type.Contains("cdrom", StringComparison.OrdinalIgnoreCase))
                    {
                        if (type.Equals("cdrom-image", StringComparison.OrdinalIgnoreCase) && File.Exists(Resolve(file))) discs.Add(Resolve(file));
                    }
                    else if (file.EndsWith(".vmdk", StringComparison.OrdinalIgnoreCase))
                        disks.Add(new ForeignDisk(Resolve(file), controllerBus));
                }
            }
        }

        var nics = new List<NicConfig>();
        for (var i = 0; i < 10; i++)
        {
            if (!On($"ethernet{i}.present")) continue;
            var dev = (Get($"ethernet{i}.virtualDev") ?? "e1000").ToLowerInvariant();
            var model = dev switch { "vmxnet3" => "vmxnet3", "e1000e" => "e1000e", "e1000" => "e1000", _ => "e1000" };
            if (model == "e1000" && dev != "e1000") notes.Add($"Network adapter {i + 1} was VMware's {dev}; it is an Intel e1000 here.");
            var connection = (Get($"ethernet{i}.connectionType") ?? "nat").ToLowerInvariant();
            if (connection is "bridged") notes.Add($"Network adapter {i + 1} was bridged; it uses NAT here until you choose a bridge in Settings.");
            if (connection is "hostonly" or "custom") notes.Add($"Network adapter {i + 1} was on a host-only/custom VMware network; it uses NAT here (a private segment in Settings is the nearest match).");
            var mac = Get($"ethernet{i}.address") ?? Get($"ethernet{i}.generatedAddress");
            nics.Add(new NicConfig { Model = model, Mac = mac?.ToLowerInvariant() });
        }

        if (nics.Count > 1) notes.Add($"VM Qontrol connects one network adapter; the other {nics.Count - 1} were left out.");
        var efi = Get("firmware") is { } fw && fw.Equals("efi", StringComparison.OrdinalIgnoreCase);
        var tpm = On("vtpm.present");
        if (tpm) notes.Add("The machine had a virtual TPM; it gets a new, empty one. If Windows used BitLocker, have its recovery key at hand.");
        if (On("vmci0.present") || Get("tools.syncTime") is not null)
            notes.Add("VMware Tools stay installed in the guest; they do nothing here. Uninstall them and install the QEMU guest tools (Summary, Guest tools).");
        if (Directory.EnumerateFiles(dir, "*.vmsn").Any())
            notes.Add("Only the machine's current state is imported; its VMware snapshots are not.");
        if (Get("checkpoint.vmState") is { Length: > 0 } || Directory.EnumerateFiles(dir, "*.vmss").Any())
            notes.Add("The machine was suspended in VMware; it starts afresh here (anything open in it is lost). Resume and shut it down in VMware first to keep it.");
        if (disks.Count == 0) notes.Add("No disk was found in the .vmx.");

        return new ForeignMachine
        {
            Kind = "VMware",
            Source = vmx,
            Name = Get("displayName") is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(vmx),
            Guest = ForeignGuest.From(guestOs),
            SourceOs = guestOs,
            MemoryMiB = long.TryParse(Get("memsize"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var mem) ? mem : null,
            Cores = int.TryParse(Get("numvcpus"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cpus) ? cpus : null,
            Firmware = efi ? FirmwareKind.Uefi : FirmwareKind.Bios,
            SecureBoot = efi && On("uefi.secureBoot.enabled"),
            Tpm = tpm,
            Disks = disks,
            Discs = discs,
            Nics = nics.Take(1).ToList(),
            Notes = notes,
        };
    }
}
