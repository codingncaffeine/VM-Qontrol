using Microsoft.Win32.SafeHandles;
using Tmds.DBus.Protocol;

namespace VmQontrol.Core.Runtime;

/// <summary>
/// The parts of systemd-logind's manager the shutdown guard uses: inhibitor locks, the time a delay lock may hold a
/// shutdown, and the signals sent before shutdown and around sleep. Rootless: any user may take these locks.
/// </summary>
public sealed class Logind(DBusConnection connection)
{
    private const string Service = "org.freedesktop.login1";
    private const string ObjectPath = "/org/freedesktop/login1";
    private const string Manager = "org.freedesktop.login1.Manager";

    /// <summary>logind on the system bus; <c>VMQONTROL_LOGIND_BUS</c> names another bus (tests run a stand-in there).</summary>
    public static Logind ForThisComputer() =>
        Environment.GetEnvironmentVariable("VMQONTROL_LOGIND_BUS") is { Length: > 0 } address
            ? new Logind(new DBusConnection(address))
            : new Logind(DBusConnection.System);

    public DBusConnection Connection => connection;

    /// <summary>Connects (a no-op on the shared system connection, which connects on first use).</summary>
    public async Task ConnectAsync()
    {
        if (connection != DBusConnection.System) await connection.ConnectAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Takes a lock: <paramref name="what"/> "shutdown", "sleep" or both ("shutdown:sleep"); <paramref name="mode"/> "delay",
    /// "block" or "block-weak". The lock holds while the returned handle stays open.
    /// </summary>
    public Task<SafeFileHandle> InhibitAsync(string what, string who, string why, string mode)
    {
        MessageBuffer message;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(Service, ObjectPath, Manager, "Inhibit", "ssss");
            writer.WriteString(what);
            writer.WriteString(who);
            writer.WriteString(why);
            writer.WriteString(mode);
            message = writer.CreateMessage();
        }

        return connection.CallMethodAsync(message, static (Message m, object? _) =>
            m.GetBodyReader().ReadHandle<SafeFileHandle>() ?? throw new InvalidOperationException("logind answered without a lock."), null);
    }

    /// <summary>
    /// A lock that warns, but does not stop, this user's own shutdown: "block-weak" (systemd 257 and later, where a plain
    /// "block" also stops the user who holds it), else "block", which before 257 meant exactly that.
    /// </summary>
    public async Task<SafeFileHandle> InhibitWarningAsync(string what, string who, string why)
    {
        try
        {
            return await InhibitAsync(what, who, why, "block-weak").ConfigureAwait(false);
        }
        catch (DBusErrorReplyException ex) when (ex.ErrorName is "org.freedesktop.DBus.Error.InvalidArgs")
        {
            return await InhibitAsync(what, who, why, "block").ConfigureAwait(false);
        }
    }

    /// <summary>How long a delay lock may hold a shutdown or sleep (InhibitDelayMaxSec; 5 s unless changed).</summary>
    public async Task<TimeSpan> DelayBudgetAsync()
    {
        MessageBuffer message;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(Service, ObjectPath, "org.freedesktop.DBus.Properties", "Get", "ss");
            writer.WriteString(Manager);
            writer.WriteString("InhibitDelayMaxUSec");
            message = writer.CreateMessage();
        }

        var usec = await connection.CallMethodAsync(message, static (Message m, object? _) => m.GetBodyReader().ReadVariantValue().GetUInt64(), null).ConfigureAwait(false);
        return TimeSpan.FromTicks((long)Math.Min(usec, (ulong)(TimeSpan.MaxValue.Ticks / 10)) * 10);
    }

    /// <summary>
    /// Calls <paramref name="handler"/> with the signal's argument for "PrepareForShutdown" or "PrepareForSleep": true
    /// before the computer goes down or to sleep, false when it wakes (or a shutdown was called off).
    /// </summary>
    public ValueTask<IDisposable> WatchAsync(string member, Action<bool> handler) =>
        connection.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Sender = Service, Path = ObjectPath, Interface = Manager, Member = member },
            static (Message m, object? _) => m.GetBodyReader().ReadBool(),
            (Notification<bool> n) =>
            {
                if (n.HasValue) handler(n.Value);
            },
            emitOnCapturedContext: false, ObserverFlags.None);
}
