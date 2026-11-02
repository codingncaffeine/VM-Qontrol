using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using VmQontrol.Core.Devices;
using VmQontrol.Core.Machines;

namespace VmQontrol.Core.Runtime;

/// <summary>A host drive connected to a running machine: the path its settings name and whether the guest may write.</summary>
public sealed record ConnectedHostDrive(string Path, bool Writable);

/// <summary>
/// Connects host drives to a running machine and takes them away again. The drive is opened here (by udisks2 when this
/// account may not open it), handed to QEMU as an open file (add-fd), and plugged into the machine's hot-plug USB
/// controller as a USB disk or disc drive: every guest has drivers for that, and it can come and go while the guest runs,
/// as a drive in a USB enclosure does. Plugging in starts with a powered-off or paused machine as well.
/// </summary>
public static class LiveHostDrives
{
    /// <summary>The USB bus VM Qontrol's own machines keep for devices handed over while running.</summary>
    public const string Bus = "hostpass.0";

    private const string Opaque = "vmq-hostdrive:";

    /// <summary>The QEMU id of the drive a path names: the same every time, so a drive is found again without a record.</summary>
    public static string DeviceId(string path) => "hostdrive-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..12];

    private static string NodeName(string path) => "hd" + DeviceId(path)["hostdrive-".Length..];

    /// <summary>The host drives connected to the machine now (from QEMU's own list of the files it was given).</summary>
    public static async Task<IReadOnlyList<ConnectedHostDrive>> ListAsync(QmpClient qmp, CancellationToken ct = default)
    {
        var list = new List<ConnectedHostDrive>();
        if (await qmp.ExecuteAsync("query-fdsets", ct: ct).ConfigureAwait(false) is not JsonArray sets) return list;
        foreach (var fd in sets.OfType<JsonObject>().SelectMany(s => s["fds"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (fd["opaque"]?.GetValue<string>() is not { } opaque || !opaque.StartsWith(Opaque, StringComparison.Ordinal)) continue;
            var rest = opaque[Opaque.Length..];
            var writable = rest.StartsWith("rw:", StringComparison.Ordinal);
            list.Add(new ConnectedHostDrive(rest[3..], writable));
        }

        return list;
    }

    /// <summary>
    /// Opens the drive and plugs it into the machine. Throws <see cref="DriveAccessException"/> when the drive is not here
    /// or may not be opened, <see cref="QmpException"/> when QEMU refuses it.
    /// </summary>
    public static async Task AttachAsync(QmpClient qmp, HostDriveConfig drive, CancellationToken ct = default)
    {
        var now = HostDrives.Find(drive.Path) ?? throw new DriveAccessException($"{Label(drive)} is not connected to this computer.");
        if (drive.Writable && now.WriteRefusal is { } refusal)
            throw new DriveAccessException($"{Label(drive)} cannot be connected for writing: {refusal}. Connect it read-only, or free it first.");
        if (now.Empty) throw new DriveAccessException($"{Label(drive)} has {(now.Kind == HostDriveKind.Optical ? "no disc in it" : "no media in it")}.");

        if ((await ListAsync(qmp, ct).ConfigureAwait(false)).Any(c => c.Path == drive.Path)) return;
        var id = DeviceId(drive.Path);
        var node = NodeName(drive.Path);
        using var file = await DriveAccess.OpenAsync(now.DevicePath, drive.Writable, ct).ConfigureAwait(false);
        var added = await qmp.ExecuteWithFileAsync("add-fd", new JsonObject { ["opaque"] = $"{Opaque}{(drive.Writable ? "rw" : "ro")}:{drive.Path}" }, file, ct).ConfigureAwait(false);
        var set = added?["fdset-id"]?.GetValue<long>() ?? throw new QmpException("add-fd", "", "QEMU did not say where it put the drive.");
        var step = "blockdev-add";
        try
        {
            await qmp.ExecuteAsync("blockdev-add", new JsonObject
            {
                ["driver"] = now.Kind == HostDriveKind.Optical ? "host_cdrom" : "host_device",
                ["node-name"] = node,
                ["filename"] = $"/dev/fdset/{set}",
                ["read-only"] = !drive.Writable,
            }, ct).ConfigureAwait(false);
            step = "device_add";
            await qmp.ExecuteAsync("device_add", new JsonObject { ["driver"] = "usb-bot", ["id"] = id, ["bus"] = Bus }, ct).ConfigureAwait(false);
            var lun = new JsonObject { ["driver"] = now.Kind == HostDriveKind.Optical ? "scsi-cd" : "scsi-hd", ["id"] = id + "-lun", ["bus"] = id + ".0", ["drive"] = node };
            if (now.Kind != HostDriveKind.Optical && now.Removable) lun["removable"] = true;
            await qmp.ExecuteAsync("device_add", lun, ct).ConfigureAwait(false);
            // A usb-bot waits for its drives before it shows itself to the guest; this is the moment it is plugged in.
            await qmp.ExecuteAsync("qom-set", new JsonObject { ["path"] = $"/machine/peripheral/{id}", ["property"] = "attached", ["value"] = true }, ct).ConfigureAwait(false);
        }
        catch (QmpException ex)
        {
            await RemoveAsync(qmp, id, node, set).ConfigureAwait(false);
            if (ex.Description.Contains("hostpass", StringComparison.Ordinal))
                throw new QmpException(step, ex.Class, "This machine has no controller for devices connected while it runs (machines imported from quickemu keep quickemu's hardware). Power it off and on once with a host drive in its settings.");
            throw;
        }

        Log.Info($"{Label(drive)} connected{(drive.Writable ? " for writing" : " read-only")}.");
    }

    /// <summary>Unplugs the drive from the machine, as pulling out a USB drive does, and closes QEMU's copy of it.</summary>
    public static async Task DetachAsync(QmpClient qmp, string path, CancellationToken ct = default)
    {
        long? set = null;
        if (await qmp.ExecuteAsync("query-fdsets", ct: ct).ConfigureAwait(false) is JsonArray sets)
        {
            foreach (var s in sets.OfType<JsonObject>())
            {
                if ((s["fds"] as JsonArray ?? []).OfType<JsonObject>().Any(f => f["opaque"]?.GetValue<string>() is { } o && o.StartsWith(Opaque, StringComparison.Ordinal) && o[(Opaque.Length + 3)..] == path))
                    set = s["fdset-id"]?.GetValue<long>();
            }
        }

        await RemoveAsync(qmp, DeviceId(path), NodeName(path), set, ct).ConfigureAwait(false);
    }

    private static async Task RemoveAsync(QmpClient qmp, string id, string node, long? set, CancellationToken ct = default)
    {
        // USB unplugging is immediate (no guest handshake as PCI has); the block node can go once the device has.
        try
        {
            await qmp.ExecuteAsync("device_del", new JsonObject { ["id"] = id }, ct).ConfigureAwait(false);
            for (var i = 0; i < 50; i++)
            {
                // The drive inside (id-lun) goes after the USB device; the block node is free only once both have.
                if (await qmp.ExecuteAsync("qom-list", new JsonObject { ["path"] = "/machine/peripheral" }, ct).ConfigureAwait(false) is JsonArray children
                    && !children.OfType<JsonObject>().Any(c => c["name"]?.GetValue<string>() is { } n && (n == id || n == id + "-lun"))) break;
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
        catch (QmpException)
        {
        }

        for (var i = 0; i < 20; i++)
        {
            try
            {
                await qmp.ExecuteAsync("blockdev-del", new JsonObject { ["node-name"] = node }, ct).ConfigureAwait(false);
                break;
            }
            catch (QmpException ex) when (ex.Description.Contains("in use", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
            catch (QmpException)
            {
                break; // never added (an attach that failed early), or already gone
            }
        }

        if (set is { } s)
        {
            try
            {
                await qmp.ExecuteAsync("remove-fd", new JsonObject { ["fdset-id"] = s }, ct).ConfigureAwait(false);
            }
            catch (QmpException)
            {
            }
        }
    }

    private static string Label(HostDriveConfig d) => d.Name.Length > 0 ? d.Name : d.Path;
}
