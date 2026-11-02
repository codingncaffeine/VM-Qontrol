using System.Diagnostics;
using System.Globalization;

namespace VmQontrol.Core.Devices;

/// <summary>
/// Lets QEMU, running as the person, open a USB device node that only root may open, the way virt-viewer and GNOME
/// Boxes do: spice-gtk's USB ACL helper asks polkit (action org.spice-space.lowlevelusbaccess, allowed for the person
/// at the computer without a password), adds an ACL for them to the node, and takes it away again when its input
/// closes. QEMU keeps the device it opened in between. Nothing is installed and no rule file is written.
/// </summary>
public static class UsbAccess
{
    /// <summary>Where distributions put the helper: Arch, Debian/Ubuntu, Fedora (per architecture), openSUSE.</summary>
    private static readonly string[] Candidates =
    [
        "/usr/lib/spice-client-glib-usb-acl-helper",
        "/usr/libexec/spice-client-glib-usb-acl-helper",
        "/usr/libexec/spice-gtk-x86_64/spice-client-glib-usb-acl-helper",
        "/usr/lib/spice-gtk/spice-client-glib-usb-acl-helper",
    ];

    public static string? Helper => Candidates.FirstOrDefault(File.Exists);

    public static bool Available => Helper is not null;

    /// <summary>
    /// Opens the device node to this account for <paramref name="hold"/> (long enough for QEMU to open it), then takes the
    /// access away; QEMU keeps the device. Returns the helper's refusal, or <c>null</c> when access was granted.
    /// </summary>
    public static async Task<string?> GrantAsync(HostUsbDevice device, TimeSpan hold, CancellationToken ct = default)
    {
        if (device.Writable) return null;
        if (Helper is not { } helper) return "spice-gtk's USB helper is not installed";
        var start = new ProcessStartInfo(helper) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new IOException("the helper did not start");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return ex.Message;
        }

        var bus = int.Parse(device.Bus, CultureInfo.InvariantCulture);
        var dev = int.Parse(device.Device, CultureInfo.InvariantCulture);
        await process.StandardInput.WriteLineAsync($"{bus} {dev}".AsMemory(), ct).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30)); // polkit may show its own dialog where the policy asks
        string? reply;
        try
        {
            reply = await process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            reply = null;
        }

        if (reply?.Trim() != "SUCCESS")
        {
            Close(process);
            return reply is { Length: > 0 } ? reply.Trim() : "access was not granted";
        }

        // The access stays while the helper runs; closing its input takes it away.
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(hold, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                Close(process);
            }
        }, CancellationToken.None);
        return null;
    }

    /// <summary>Grants every device in <paramref name="devices"/> this account may not open; returns the refusals.</summary>
    public static async Task<IReadOnlyList<string>> GrantAllAsync(IEnumerable<HostUsbDevice> devices, TimeSpan hold, CancellationToken ct = default)
    {
        var refused = new List<string>();
        foreach (var d in devices.Where(d => !d.Writable))
            if (await GrantAsync(d, hold, ct).ConfigureAwait(false) is { } why) refused.Add($"{d.Name}: {why}");
        return refused;
    }

    private static void Close(Process process)
    {
        try
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(2000)) process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
        }
        finally
        {
            process.Dispose();
        }
    }
}
