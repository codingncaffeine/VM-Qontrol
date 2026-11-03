using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace VmQontrol.Core.Hosts;

/// <summary>
/// What a machine with PCI devices needs from this computer, and getting it: each device on vfio-pci, its IOMMU group's
/// /dev/vfio file open to this account, and a memory-lock limit as large as the machine (VFIO pins all its memory). One
/// polkit prompt does all three (pkexec running a short script), then waits for that QEMU to exit and gives the devices
/// back to their own drivers, as libvirt's managed passthrough does.
/// </summary>
public static partial class Vfio
{
    /// <summary>Plain sentences for what is missing; empty when the machine can start as it is.</summary>
    public static IReadOnlyList<string> Missing(IReadOnlyList<string> addresses, long memoryMiB, IReadOnlyList<PciDevice> devices, string dev = "/dev", ulong? memlockBytes = null)
    {
        var missing = new List<string>();
        foreach (var a in addresses)
        {
            var d = devices.FirstOrDefault(x => x.Address == a);
            if (d is null) missing.Add($"The PCI device {a} is not in this computer.");
            else if (d.Group is null) missing.Add($"{d.Name} has no IOMMU group: turn on IOMMU (AMD-Vi or VT-d) in the firmware and the kernel's options.");
            else if (!d.BoundToVfio) missing.Add($"{d.Name} is still with this computer ({d.Driver ?? "no driver"}); it is handed to vfio-pci at power on.");
            else if (!CanOpen(Path.Combine(dev, "vfio", d.Group))) missing.Add($"{d.Name}'s group file /dev/vfio/{d.Group} is not open to this account; it is at power on.");
        }

        var limit = memlockBytes ?? MemlockLimit();
        var need = (ulong)(memoryMiB + 256) * 1024 * 1024; // the machine's memory, and the device's own mappings
        if (addresses.Count > 0 && limit < need)
            missing.Add($"The memory-lock limit is {Describe(limit)}; a machine with PCI devices locks all of its {memoryMiB / 1024.0:0.#} GB. It is raised for VM Qontrol at power on (to keep it raised: a memlock line in /etc/security/limits.conf).");
        return missing;
    }

    /// <summary>Only what cannot be fixed at power on (a device that is not here, no IOMMU): those refuse the start.</summary>
    public static IReadOnlyList<string> Blocking(IReadOnlyList<string> addresses, IReadOnlyList<PciDevice> devices) =>
        addresses.Select(a => (a, d: devices.FirstOrDefault(x => x.Address == a))).Where(x => x.d is null || x.d.Group is null)
            .Select(x => x.d is null ? $"The PCI device {x.a} is not in this computer." : $"{x.d.Name} has no IOMMU group: turn on IOMMU (AMD-Vi or VT-d) in the firmware settings, and amd_iommu=on or intel_iommu=on in the kernel's options.")
            .ToList();

    /// <summary>
    /// The root script: bind each device to vfio-pci, give the account the group files, raise this process's memory-lock
    /// limit (QEMU, started next, inherits it), then in the background wait for QEMU (its pid file) and give the devices back.
    /// Arguments: $1 user, $2 our pid, $3 QEMU pid file, then the devices.
    /// </summary>
    public const string Script = """
        set -e
        sys="${VMQ_SYS:-/sys}"; devdir="${VMQ_DEV:-/dev}"; prlimit="${VMQ_PRLIMIT:-prlimit}"
        user="$1"; app="$2"; pidfile="$3"; shift 3
        modprobe vfio-pci 2>/dev/null || true
        for dev in "$@"; do
          d="$sys/bus/pci/devices/$dev"
          echo vfio-pci > "$d/driver_override"
          if [ -e "$d/driver" ]; then echo "$dev" > "$d/driver/unbind"; fi
          echo "$dev" > "$sys/bus/pci/drivers_probe"
          g=$(basename "$(readlink "$d/iommu_group")")
          for i in $(seq 50); do [ -e "$devdir/vfio/$g" ] && break; sleep 0.1; done
          chown "$user" "$devdir/vfio/$g"
        done
        "$prlimit" --pid "$app" --memlock=unlimited:unlimited
        (
          for i in $(seq 600); do [ -s "$pidfile" ] && break; sleep 0.1; done
          qemu=$(cat "$pidfile" 2>/dev/null || true)
          if [ -n "$qemu" ]; then while kill -0 "$qemu" 2>/dev/null; do sleep 1; done; fi
          for dev in "$@"; do
            d="$sys/bus/pci/devices/$dev"
            echo > "$d/driver_override"
            if [ -e "$d/driver" ]; then echo "$dev" > "$d/driver/unbind"; fi
            echo "$dev" > "$sys/bus/pci/drivers_probe"
          done
        ) > /dev/null 2>&1 < /dev/null &
        """;

    /// <summary>Runs <see cref="Script"/> through pkexec (the password is asked for); returns why it did not happen, or <c>null</c>.</summary>
    public static async Task<string?> PrepareAsync(IReadOnlyList<string> addresses, string qemuPidFile, CancellationToken ct = default)
    {
        if (!Devices.DriveAccess.Interactive) return "polkit would ask for an administrator's password to hand the devices over.";
        if (!File.Exists("/usr/bin/pkexec")) return "polkit's pkexec is not installed, so the password cannot be asked for";
        var start = new ProcessStartInfo("/usr/bin/pkexec") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in (string[])["/bin/sh", "-c", Script, "sh", Environment.UserName, Environment.ProcessId.ToString(CultureInfo.InvariantCulture), qemuPidFile, .. addresses])
            start.ArgumentList.Add(a);
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

    /// <summary>This process's memory-lock limit (soft), in bytes.</summary>
    public static unsafe ulong MemlockLimit()
    {
        var limit = stackalloc ulong[2];
        return GetRlimit(8 /* RLIMIT_MEMLOCK */, (nint)limit) == 0 ? limit[0] : 0;
    }

    private static string Describe(ulong bytes) => bytes == ulong.MaxValue ? "unlimited" : bytes >= 1UL << 30 ? $"{bytes / (1UL << 30)} GB" : $"{bytes / (1UL << 20)} MB";

    private static bool CanOpen(string path) => File.Exists(path) && Access(path, 6) == 0;

    [LibraryImport("libc", EntryPoint = "getrlimit", SetLastError = true)]
    private static partial int GetRlimit(int resource, nint limit);

    [LibraryImport("libc", EntryPoint = "access", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Access(string path, int mode);
}
