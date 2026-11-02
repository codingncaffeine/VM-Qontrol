using System.Text.Json;
using System.Text.Json.Serialization;

namespace VmQontrol.Core;

/// <summary>The app's own settings (settings.json): where machines live, favourites and folders, and display choices.</summary>
public sealed record AppSettings
{
    /// <summary>Folders scanned for machines; the first is where new machines go.</summary>
    public List<string> LibraryRoots { get; init; } = [];

    public HashSet<string> Favourites { get; init; } = [];

    /// <summary>Machine id → the sidebar folder it is filed under.</summary>
    public Dictionary<string, string> Folders { get; init; } = [];

    /// <summary>"auto", "remote-viewer" or "spicy".</summary>
    public string Viewer { get; init; } = "auto";

    /// <summary>"system", "dark" or "light".</summary>
    public string Theme { get; init; } = "system";

    public bool WelcomeDismissed { get; init; }

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
