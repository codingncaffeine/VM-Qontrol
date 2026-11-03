using System.Text.Json;
using System.Text.Json.Serialization;

namespace VmQontrol.Core;

/// <summary>The app's own settings (settings.json): where machines live, favorites and folders, and display choices.</summary>
public sealed record AppSettings
{
    /// <summary>Folders scanned for machines; the first is where new machines go.</summary>
    public List<string> LibraryRoots { get; set; } = [];

    public HashSet<string> Favorites { get; set; } = [];

    /// <summary>Settings saved before 2026-11-03 spelled it "favourites": read once, never written.</summary>
    [JsonPropertyName("favourites")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HashSet<string>? FavoritesBefore
    {
        get => null;
        set
        {
            if (value is not null) Favorites.UnionWith(value);
        }
    }

    /// <summary>The library's groups (the sidebar's headings), in the order the person put them; a group may be empty.</summary>
    public List<string> Groups { get; set; } = [];

    /// <summary>Machine id → the group it is in (a name from <see cref="Groups"/>); a machine not listed is in none.</summary>
    public Dictionary<string, string> GroupOf { get; set; } = [];

    /// <summary>Groups shown folded up in the sidebar.</summary>
    public HashSet<string> CollapsedGroups { get; set; } = [];

    /// <summary>Settings saved before groups (2026-11-03) called them folders: read once into <see cref="GroupOf"/>, never written.</summary>
    [JsonPropertyName("folders")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? FoldersBefore
    {
        get => null;
        set
        {
            if (value is null) return;
            foreach (var (id, group) in value) GroupOf.TryAdd(id, group);
        }
    }

    /// <summary>Every group in order: <see cref="Groups"/>, then any a machine names that the list lacks (older settings).</summary>
    public IReadOnlyList<string> OrderedGroups() =>
        Groups.Concat(GroupOf.Values.Where(g => !Groups.Contains(g, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).Order(StringComparer.CurrentCultureIgnoreCase))
            .Where(g => g.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>"auto", "remote-viewer" or "spicy".</summary>
    public string Viewer { get; set; } = "auto";

    /// <summary>"system", "dark" or "light".</summary>
    public string Theme { get; set; } = "system";

    public bool WelcomeDismissed { get; set; }

    /// <summary>The VirtIO driver disc (virtio-win.iso) last chosen, offered again for the next Windows machine.</summary>
    public string? DriversIso { get; set; }

    /// <summary>USB devices ("vendor:product") never offered to a machine when plugged in (Don't ask about this device).</summary>
    public HashSet<string> UsbNeverAsk { get; set; } = [];

    /// <summary>Private segments' subnets and address servers (the virtual network editor); a segment not listed gets the defaults.</summary>
    public List<Runtime.SegmentConfig> Segments { get; set; } = [];

    /// <summary>The NAT network inside machines, when QEMU's 10.0.2.0/24 clashes with a network a guest must reach; <c>null</c> = QEMU's.</summary>
    public string? NatSubnet { get; set; }

    /// <summary>Ask before Power off (pulling the plug).</summary>
    public bool ConfirmPowerOff { get; set; } = true;

    /// <summary>Ask before Reset.</summary>
    public bool ConfirmReset { get; set; } = true;

    /// <summary>Open the app minimized when it starts at sign-in.</summary>
    public bool StartMinimized { get; set; }

    /// <summary>While machines run, the desktop's shut-down dialog names them (a warning; this user's own shutdown still goes ahead).</summary>
    public bool WarnAtHostShutdown { get; set; } = true;

    /// <summary>When this computer shuts down with machines running, suspend each in the time logind allows.</summary>
    public bool SaveAtHostShutdown { get; set; } = true;

    /// <summary>An icon in the desktop's tray with the machines; the window's close button then keeps VM Qontrol there.</summary>
    public bool TrayIcon { get; set; }

    /// <summary>Settings saved before 2026-11-03 spelled it "startMinimised": read once, never written.</summary>
    [JsonPropertyName("startMinimised")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? StartMinimizedBefore
    {
        get => null;
        set
        {
            if (value is { } v) StartMinimized = v;
        }
    }

    /// <summary>Warn before powering on a machine that would leave this computer short of memory.</summary>
    public bool WarnLowMemory { get; set; } = true;

    /// <summary>Memory kept free for the computer itself, in GB; <c>null</c> = automatic (an eighth of it, 4–16 GB).</summary>
    public int? KeepFreeGB { get; set; }

    /// <summary>"lighter", "recommended" or "generous": how big the wizard makes new machines.</summary>
    public string NewMachineSize { get; set; } = "recommended";

    /// <summary>The wizard's "Power on the machine when it is created" starts ticked.</summary>
    public bool NewMachinePowerOn { get; set; } = true;

    /// <summary>The wizard's Lean Windows switch starts on.</summary>
    public bool NewMachineLean { get; set; } = true;

    /// <summary>The newest release the last Check for updates found ("0.0.6"), its page, and when it was asked.</summary>
    public string? LatestKnownVersion { get; set; }

    public string? LatestKnownUrl { get; set; }

    public DateTimeOffset? LatestCheckedAt { get; set; }

    /// <summary>The main window as it was last closed: its size, whether it was maximized, and (X11 only) its place.</summary>
    public WindowPlacement? MainWindow { get; set; }

    /// <summary>The size choice as the recommendations take it.</summary>
    [JsonIgnore]
    public Machines.MachineSize Size => NewMachineSize switch { "lighter" => Machines.MachineSize.Lighter, "generous" => Machines.MachineSize.Generous, _ => Machines.MachineSize.Recommended };

    /// <summary>What stays free for this computer, in MiB, for a computer with <paramref name="totalMiB"/>.</summary>
    public long KeepFreeMiB(long totalMiB) => KeepFreeGB is { } gb ? gb * 1024L : Hosts.MemoryHeadroom.AutomaticKeepFreeMiB(totalMiB);

    /// <summary>Why the settings file could not be read at the last Load (its copy is kept beside it); <c>null</c> = it was fine.</summary>
    [JsonIgnore]
    public string? LoadProblem { get; init; }

    public static AppSettings Load(QontrolPaths paths)
    {
        string problem;
        try
        {
            if (File.Exists(paths.SettingsFile))
            {
                var loaded = JsonSerializer.Deserialize(File.ReadAllText(paths.SettingsFile), AppSettingsJson.Default.AppSettings)
                             ?? throw new JsonException("the file holds no settings");
                loaded = loaded.Normalized();
                return loaded.LibraryRoots.Count > 0 ? loaded : loaded with { LibraryRoots = [paths.DefaultLibrary] };
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            // Never written over: the file is kept as it is, beside the new one, and the app says so.
            var kept = $"{paths.SettingsFile}.bad-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                File.Copy(paths.SettingsFile, kept, overwrite: false);
            }
            catch (Exception copy) when (copy is IOException or UnauthorizedAccessException)
            {
                kept = "(it could not be copied)";
            }

            problem = $"The settings file could not be read ({ex.Message}). It was kept as {kept}; VM Qontrol started with its defaults, so library folders, favorites and folders may need adding again.";
            Log.Warn(problem);
            return new AppSettings { LibraryRoots = [paths.DefaultLibrary], LoadProblem = problem };
        }

        return new AppSettings { LibraryRoots = [paths.DefaultLibrary] };
    }

    /// <summary>A file written by hand (or by an older build) may hold null where a list belongs: each becomes empty.</summary>
    private AppSettings Normalized() => this with
    {
        LibraryRoots = (LibraryRoots ?? []).Where(r => !string.IsNullOrWhiteSpace(r) && Path.IsPathRooted(r)).ToList(),
        Favorites = Favorites ?? [],
        Groups = (Groups ?? []).Where(g => !string.IsNullOrWhiteSpace(g)).ToList(),
        GroupOf = (GroupOf ?? []).Where(p => p.Key is not null && !string.IsNullOrWhiteSpace(p.Value)).ToDictionary(p => p.Key, p => p.Value),
        CollapsedGroups = CollapsedGroups ?? [],
        UsbNeverAsk = UsbNeverAsk ?? [],
        Segments = (Segments ?? []).Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Name)).ToList(),
        Viewer = string.IsNullOrWhiteSpace(Viewer) ? "auto" : Viewer,
        Theme = string.IsNullOrWhiteSpace(Theme) ? "system" : Theme,
        MainWindow = MainWindow?.Sane(),
    };

    private static readonly Lock SaveLock = new();

    /// <summary>
    /// Writes the settings whole or not at all (a new file, flushed to disk, then renamed over the old one). Saves from
    /// several threads queue behind one lock, so none is lost to another's half-written file.
    /// </summary>
    public void Save(QontrolPaths paths)
    {
        lock (SaveLock)
        {
            Directory.CreateDirectory(paths.Config);
            var temp = $"{paths.SettingsFile}.{Environment.ProcessId}.new";
            using (var stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, this, AppSettingsJson.Default.AppSettings);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, paths.SettingsFile, overwrite: true);
        }
    }

    private static AppSettings? _pending;
    private static Task _lastSave = Task.CompletedTask;

    /// <summary>
    /// Waits (at most <paramref name="timeout"/>) for saves asked for in the background: the app calls it as it exits,
    /// once its windows are gone, so the last change (the window's size at close) is not lost with the process.
    /// </summary>
    public static bool WaitForSaves(TimeSpan timeout) => Volatile.Read(ref _lastSave).Wait(timeout);

    /// <summary>
    /// Saves on a worker. Saves asked for in a row write the newest settings only (an older copy never lands last); a
    /// failure is logged, and the change stays in memory for the next save.
    /// </summary>
    public void SaveInBackground(QontrolPaths paths)
    {
        Volatile.Write(ref _pending, this);
        Volatile.Write(ref _lastSave, Task.Run(() =>
        {
            lock (SaveLock)
            {
                if (Interlocked.Exchange(ref _pending, null) is not { } newest) return;
                try
                {
                    newest.Save(paths);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"Settings could not be saved: {ex.Message}");
                }
            }
        }));
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJson : JsonSerializerContext;

/// <summary>A window's size in device-independent pixels, maximized or not, and its place on screen when the desktop lets
/// an app place itself (X11; Wayland compositors place windows themselves).</summary>
public sealed record WindowPlacement
{
    public double Width { get; set; }

    public double Height { get; set; }

    public int? X { get; set; }

    public int? Y { get; set; }

    public bool Maximized { get; set; }

    /// <summary>A hand-edited or damaged size is dropped rather than opening a window nobody can see.</summary>
    internal WindowPlacement? Sane() =>
        double.IsFinite(Width) && double.IsFinite(Height) && Width is >= 200 and <= 32768 && Height is >= 200 and <= 32768 ? this : null;
}
