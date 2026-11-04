using System.Text;
using Tmds.DBus.Protocol;

namespace VmQontrol.Core.WindowsApps;

/// <summary>
/// The desktop's keyring through the freedesktop Secret Service (GNOME Keyring, KDE Wallet's ksecretd, KeePassXC, oo7),
/// the store Remmina, GNOME Connections and NetworkManager keep passwords in. D43: a Windows account's password lives here,
/// never in a VM Qontrol file; it reaches FreeRDP on its standard input. The "plain" session algorithm is the one every
/// provider offers; the bus is this account's own session bus. A locked keyring is unlocked through the provider's own
/// prompt. With no Secret Service at all (a bare window manager), nothing is stored and the password is asked each time.
/// </summary>
public sealed class Keyring(DBusConnection connection)
{
    private const string Service = "org.freedesktop.secrets";
    private const string ServicePath = "/org/freedesktop/secrets";
    private const string ServiceInterface = "org.freedesktop.Secret.Service";
    private const string DefaultCollection = "/org/freedesktop/secrets/aliases/default";

    /// <summary>The session bus; <c>VMQONTROL_KEYRING_BUS</c> names another (tests run a keyring on a private bus).</summary>
    public static Keyring ForThisAccount() =>
        Environment.GetEnvironmentVariable("VMQONTROL_KEYRING_BUS") is { Length: > 0 } address
            ? new Keyring(new DBusConnection(address))
            : new Keyring(DBusConnection.Session ?? throw new InvalidOperationException("There is no session bus."));

    private bool _connected;

    private async Task ConnectAsync()
    {
        if (_connected) return;
        if (connection != DBusConnection.Session) await connection.ConnectAsync().ConfigureAwait(false);
        _connected = true;
    }

    /// <summary>The attributes a machine's Windows password is filed under.</summary>
    public static IReadOnlyDictionary<string, string> Attributes(string machineId, string account) => new Dictionary<string, string>
    {
        ["application"] = "vm-qontrol",
        ["kind"] = "windows-apps",
        ["machine"] = machineId,
        ["account"] = account,
    };

    /// <summary>Whether a Secret Service answers on the bus.</summary>
    public async Task<bool> AvailableAsync() => await WhyUnavailableAsync().ConfigureAwait(false) is null;

    /// <summary><c>null</c> when a Secret Service answers on the bus, else why not.</summary>
    public async Task<string?> WhyUnavailableAsync()
    {
        try
        {
            await ConnectAsync().ConfigureAwait(false);
            await OpenSessionAsync().ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.Message;
        }
    }

    /// <summary>The stored password, or <c>null</c> when there is none (or the keyring stayed locked).</summary>
    public async Task<string?> LookupAsync(IReadOnlyDictionary<string, string> attributes, CancellationToken ct = default)
    {
        await ConnectAsync().ConfigureAwait(false);
        var session = await OpenSessionAsync().ConfigureAwait(false);
        var (unlocked, locked) = await SearchAsync(attributes).ConfigureAwait(false);
        var items = unlocked.ToList();
        if (items.Count == 0 && locked.Count > 0) items.AddRange(await UnlockAsync(locked, ct).ConfigureAwait(false));
        if (items.Count == 0) return null;

        MessageBuffer message;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(Service, ServicePath, ServiceInterface, "GetSecrets", "aoo");
            writer.WriteArray(items.Take(1).Select(p => new ObjectPath(p)).ToArray());
            writer.WriteObjectPath(session);
            message = writer.CreateMessage();
        }

        var secret = await connection.CallMethodAsync(message, static (Message m, object? _) =>
        {
            var reader = m.GetBodyReader();
            var end = reader.ReadDictionaryStart();
            while (reader.HasNext(end))
            {
                reader.AlignStruct();
                reader.ReadObjectPath();
                reader.AlignStruct();
                reader.ReadObjectPath();
                reader.ReadArrayOfByte();
                var value = reader.ReadArrayOfByte();
                reader.ReadString();
                return value;
            }

            return null;
        }, null).ConfigureAwait(false);
        return secret is null ? null : Encoding.UTF8.GetString(secret);
    }

    /// <summary>Stores (or replaces) the password in the default collection.</summary>
    public async Task StoreAsync(string label, IReadOnlyDictionary<string, string> attributes, string password, CancellationToken ct = default)
    {
        await ConnectAsync().ConfigureAwait(false);
        var session = await OpenSessionAsync().ConfigureAwait(false);
        MessageBuffer message;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(Service, DefaultCollection, "org.freedesktop.Secret.Collection", "CreateItem", "a{sv}(oayays)b");
            var properties = writer.WriteDictionaryStart();
            writer.WriteDictionaryEntryStart();
            writer.WriteString("org.freedesktop.Secret.Item.Label");
            writer.WriteVariantString(label);
            writer.WriteDictionaryEntryStart();
            writer.WriteString("org.freedesktop.Secret.Item.Attributes");
            writer.WriteSignature("a{ss}");
            var map = writer.WriteDictionaryStart();
            foreach (var (key, value) in attributes)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString(key);
                writer.WriteString(value);
            }

            writer.WriteDictionaryEnd(map);
            writer.WriteDictionaryEnd(properties);
            writer.WriteStructureStart();
            writer.WriteObjectPath(session);
            writer.WriteArray(Array.Empty<byte>());
            writer.WriteArray(Encoding.UTF8.GetBytes(password));
            writer.WriteString("text/plain; charset=utf8");
            writer.WriteBool(true);
            message = writer.CreateMessage();
        }

        var prompt = await connection.CallMethodAsync(message, static (Message m, object? _) =>
        {
            var reader = m.GetBodyReader();
            reader.ReadObjectPath();
            return reader.ReadObjectPathAsString();
        }, null).ConfigureAwait(false);
        if (prompt != "/" && !await PromptAsync(prompt, ct).ConfigureAwait(false))
            throw new InvalidOperationException("The keyring's prompt was dismissed, so the password was not stored.");
    }

    /// <summary>Removes every stored password filed under these attributes.</summary>
    public async Task DeleteAsync(IReadOnlyDictionary<string, string> attributes, CancellationToken ct = default)
    {
        await ConnectAsync().ConfigureAwait(false);
        var (unlocked, locked) = await SearchAsync(attributes).ConfigureAwait(false);
        foreach (var item in unlocked.Concat(locked))
        {
            MessageBuffer message;
            using (var writer = connection.GetMessageWriter())
            {
                writer.WriteMethodCallHeader(Service, item, "org.freedesktop.Secret.Item", "Delete");
                message = writer.CreateMessage();
            }

            var prompt = await connection.CallMethodAsync(message, static (Message m, object? _) => m.GetBodyReader().ReadObjectPathAsString(), null).ConfigureAwait(false);
            if (prompt != "/") await PromptAsync(prompt, ct).ConfigureAwait(false);
        }
    }

    private async Task<string> OpenSessionAsync()
    {
        MessageBuffer message;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(Service, ServicePath, ServiceInterface, "OpenSession", "sv");
            writer.WriteString("plain");
            writer.WriteVariantString("");
            message = writer.CreateMessage();
        }

        return await connection.CallMethodAsync(message, static (Message m, object? _) =>
        {
            var reader = m.GetBodyReader();
            reader.ReadVariantValue();
            return reader.ReadObjectPathAsString();
        }, null).ConfigureAwait(false);
    }

    private async Task<(IReadOnlyList<string> Unlocked, IReadOnlyList<string> Locked)> SearchAsync(IReadOnlyDictionary<string, string> attributes)
    {
        MessageBuffer message;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(Service, ServicePath, ServiceInterface, "SearchItems", "a{ss}");
            var map = writer.WriteDictionaryStart();
            foreach (var (key, value) in attributes)
            {
                writer.WriteDictionaryEntryStart();
                writer.WriteString(key);
                writer.WriteString(value);
            }

            writer.WriteDictionaryEnd(map);
            message = writer.CreateMessage();
        }

        return await connection.CallMethodAsync(message, static (Message m, object? _) =>
        {
            var reader = m.GetBodyReader();
            IReadOnlyList<string> unlocked = reader.ReadArrayOfObjectPath().Select(p => p.ToString()).ToList();
            IReadOnlyList<string> locked = reader.ReadArrayOfObjectPath().Select(p => p.ToString()).ToList();
            return (unlocked, locked);
        }, null).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<string>> UnlockAsync(IReadOnlyList<string> items, CancellationToken ct)
    {
        MessageBuffer message;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(Service, ServicePath, ServiceInterface, "Unlock", "ao");
            writer.WriteArray(items.Select(p => new ObjectPath(p)).ToArray());
            message = writer.CreateMessage();
        }

        var (unlocked, prompt) = await connection.CallMethodAsync(message, static (Message m, object? _) =>
        {
            var reader = m.GetBodyReader();
            IReadOnlyList<string> done = reader.ReadArrayOfObjectPath().Select(p => p.ToString()).ToList();
            return (done, reader.ReadObjectPathAsString());
        }, null).ConfigureAwait(false);
        if (unlocked.Count > 0 || prompt == "/") return unlocked;
        return await PromptAsync(prompt, ct).ConfigureAwait(false) ? items : [];
    }

    /// <summary>Shows the provider's own prompt and waits for it; false when it was dismissed.</summary>
    private async Task<bool> PromptAsync(string prompt, CancellationToken ct)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = await connection.AddMatchAsync(
            new MatchRule { Type = MessageType.Signal, Path = prompt, Interface = "org.freedesktop.Secret.Prompt", Member = "Completed" },
            static (Message m, object? _) => m.GetBodyReader().ReadBool(),
            (Notification<bool> n) =>
            {
                if (n.HasValue) done.TrySetResult(!n.Value);
            },
            emitOnCapturedContext: false, ObserverFlags.None).ConfigureAwait(false);
        MessageBuffer message;
        using (var writer = connection.GetMessageWriter())
        {
            writer.WriteMethodCallHeader(Service, prompt, "org.freedesktop.Secret.Prompt", "Prompt", "s");
            writer.WriteString("");
            message = writer.CreateMessage();
        }

        await connection.CallMethodAsync(message).ConfigureAwait(false);
        using var registration = ct.Register(() => done.TrySetCanceled(ct));
        return await done.Task.ConfigureAwait(false);
    }
}
