using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace VmQontrol.Core.Devices;

/// <summary>The drive could not be opened; <see cref="Message"/> is a plain sentence for the person.</summary>
public sealed class DriveAccessException(string message) : Exception(message);

/// <summary>
/// Opens a host drive for a machine, as GNOME Disks and GNOME Boxes do: when this account may not open the device node
/// (whole disks belong to root and the "disk" group), udisks2 opens it on its behalf after polkit asks for an
/// administrator's password once (polkit keeps the answer for a few minutes), and hands back the open file. QEMU, running
/// as the person, is given that open file; nothing is installed, no group changes, no rule file is written. A writable
/// drive is opened exclusively, so the host cannot mount it while the guest writes to it.
/// </summary>
public static partial class DriveAccess
{
    private const int ORdOnly = 0, ORdWr = 2, OExcl = 0x80, OCloexec = 0x80000;
    private const string UDisks = "org.freedesktop.UDisks2";

    /// <summary>Opens <paramref name="devicePath"/> read-only, or for reading and writing when <paramref name="writable"/>.</summary>
    public static async Task<SafeFileHandle> OpenAsync(string devicePath, bool writable, CancellationToken ct = default)
    {
        var flags = (writable ? ORdWr | OExcl : ORdOnly) | OCloexec;
        var fd = Open(devicePath, flags);
        if (fd >= 0) return new SafeFileHandle(fd, ownsHandle: true);
        var errno = Marshal.GetLastPInvokeError();
        if (errno == 16) throw new DriveAccessException($"{devicePath} is busy: this computer has it open (mounted, or used by another program). Unmount it first.");
        if (errno is not (13 or 1)) throw new DriveAccessException($"{devicePath} could not be opened ({Marshal.GetPInvokeErrorMessage(errno)}).");
        return await OpenThroughUDisksAsync(devicePath, writable, ct).ConfigureAwait(false);
    }

    private static async Task<SafeFileHandle> OpenThroughUDisksAsync(string devicePath, bool writable, CancellationToken ct)
    {
        var connection = DBusConnection.System;
        try
        {
            var objectPath = await ResolveAsync(connection, devicePath).WaitAsync(ct).ConfigureAwait(false)
                             ?? throw new DriveAccessException($"udisks2 does not know {devicePath}.");
            // polkit's own password dialog may be on screen while this waits.
            return await connection.CallMethodAsync(OpenMessage(connection, objectPath, writable), static (Message m, object? _) =>
            {
                var reader = m.GetBodyReader();
                return reader.ReadHandle<SafeFileHandle>() ?? throw new DriveAccessException("udisks2 answered without an open file.");
            }, null).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException ex)
        {
            throw new DriveAccessException(ex.ErrorName switch
            {
                "org.freedesktop.UDisks2.Error.NotAuthorizedDismissed" => "The password prompt was closed, so the drive was not opened.",
                "org.freedesktop.UDisks2.Error.NotAuthorized" or "org.freedesktop.UDisks2.Error.NotAuthorizedCanObtain" => "This account is not allowed to open the drive (polkit refused).",
                "org.freedesktop.DBus.Error.ServiceUnknown" => "udisks2 is not running, so the drive cannot be opened for this account. The host check names the package.",
                _ when ex.ErrorMessage.Contains("busy", StringComparison.OrdinalIgnoreCase) => $"{devicePath} is busy: this computer has it open (mounted, or used by another program). Unmount it first.",
                _ => $"udisks2 could not open the drive: {ex.ErrorMessage}",
            });
        }
        catch (DBusConnectionException)
        {
            throw new DriveAccessException("The system bus is not reachable, so udisks2 cannot open the drive for this account.");
        }
    }

    private static MessageBuffer OpenMessage(DBusConnection connection, string objectPath, bool writable)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(UDisks, objectPath, "org.freedesktop.UDisks2.Block", "OpenDevice", "sa{sv}", MessageFlags.AllowInteractiveAuthorization);
        writer.WriteString(writable ? "rw" : "r");
        var dict = writer.WriteDictionaryStart();
        if (writable)
        {
            writer.WriteDictionaryEntryStart();
            writer.WriteString("flags");
            writer.WriteVariantInt32(OExcl);
        }

        writer.WriteDictionaryEnd(dict);
        return writer.CreateMessage();
    }

    private static MessageBuffer ResolveMessage(DBusConnection connection, string devicePath)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(UDisks, "/org/freedesktop/UDisks2/Manager", "org.freedesktop.UDisks2.Manager", "ResolveDevice", "a{sv}a{sv}");
        var spec = writer.WriteDictionaryStart();
        writer.WriteDictionaryEntryStart();
        writer.WriteString("path");
        writer.WriteVariantString(devicePath);
        writer.WriteDictionaryEnd(spec);
        writer.WriteDictionaryEnd(writer.WriteDictionaryStart());
        return writer.CreateMessage();
    }

    private static async Task<string?> ResolveAsync(DBusConnection connection, string devicePath)
    {
        var paths = await connection.CallMethodAsync(ResolveMessage(connection, devicePath), static (Message m, object? _) =>
        {
            var reader = m.GetBodyReader();
            return reader.ReadArrayOfObjectPath();
        }, null).ConfigureAwait(false);
        return paths.Length > 0 ? paths[0].ToString() : null;
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);
}
