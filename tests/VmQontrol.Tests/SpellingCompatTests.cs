using VmQontrol.Core;

namespace VmQontrol.Tests;

/// <summary>Settings saved with the British spelling (before 2026-11-03) keep their favorites and start-minimized choice.</summary>
public sealed class SpellingCompatTests
{
    [Fact]
    public void Old_settings_keep_their_favorites_and_are_saved_in_the_new_spelling()
    {
        using var profile = new TestProfile("spelling");
        Directory.CreateDirectory(profile.Paths.Config);
        File.WriteAllText(profile.Paths.SettingsFile, """{ "favourites": [ "abc" ], "startMinimised": true }""");
        var settings = AppSettings.Load(profile.Paths);
        Assert.Equal(["abc"], settings.Favorites);
        Assert.True(settings.StartMinimized);
        settings.Save(profile.Paths);
        var text = File.ReadAllText(profile.Paths.SettingsFile);
        Assert.DoesNotContain("favourites", text);
        Assert.DoesNotContain("Minimised", text);
        Assert.Contains("\"favorites\"", text);
        Assert.Equal(["abc"], AppSettings.Load(profile.Paths).Favorites);
    }
}
