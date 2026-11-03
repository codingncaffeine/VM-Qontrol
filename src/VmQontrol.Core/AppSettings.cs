using System.Text.Json;
using System.Text.Json.Serialization;

namespace VmQontrol.Core;

/// <summary>The app's own settings (settings.json): where machines live, favourites and folders, and display choices.</summary>
public sealed record AppSettings
{
    /// <summary>Folders scanned for machines; the first is where new machines go.</summary>
    public List<string> LibraryRoots { get; set; } = [];

    public HashSet<string> Favourites { get; set; } = [];

    /// <summary>Machine id → the sidebar folder it is filed under.</summary>
    public Dictionary<string, string> Folders { get; set; } = [];

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

    /// <summary>Open the app minimised when it starts at sign-in.</summary>
    public bool StartMinimised { get; set; }

    public static AppSettings Load(QontrolPaths paths)
    {
        try
        {
            if (File.Exists(paths.SettingsFile))
            {
                var loaded = JsonSerializer.Deserialize(File.ReadAllText(paths.SettingsFile), AppSettingsJson.Default.AppSettings);
                if (loaded is not null) return loaded.LibraryRoots.Count > 0 ? loaded : loaded with { LibraryRoots = [paths.DefaultLibrary] };
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"Settings could not be read ({ex.Message}); using the defaults.");
        }

        return new AppSettings { LibraryRoots = [paths.DefaultLibrary] };
    }

    public void Save(QontrolPaths paths)
    {
        Directory.CreateDirectory(paths.Config);
        var temp = paths.SettingsFile + ".new";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, AppSettingsJson.Default.AppSettings));
        File.Move(temp, paths.SettingsFile, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJson : JsonSerializerContext;
