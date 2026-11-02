using VmQontrol.App.Platform;

namespace VmQontrol.Tests;

/// <summary>Self-registration with the desktop, always into throwaway folders (never the real ~/.local/share).</summary>
public sealed class DesktopIntegrationTests
{
    private const string Name = "io.github.codingncaffeine.VMQontrol";

    private static (string Home, string System, string Program) Folders(TestProfile profile)
    {
        var home = Path.Combine(profile.Root, "data-home");
        var system = Path.Combine(profile.Root, "usr-share");
        var program = Path.Combine(profile.Root, "Dev Folder", "vmqontrol"); // a space, as in the real dev folder
        Directory.CreateDirectory(Path.GetDirectoryName(program)!);
        File.WriteAllText(program, "");
        return (home, system, program);
    }

    [Fact]
    public void A_build_without_a_package_registers_its_entry_and_icons()
    {
        using var profile = new TestProfile("desktop-register");
        var (home, system, program) = Folders(profile);
        DesktopIntegration.Ensure(home, [system], program);

        var entry = File.ReadAllText(Path.Combine(home, "applications", Name + ".desktop"));
        Assert.Contains($"Exec=\"{program}\" %U", entry, StringComparison.Ordinal);
        Assert.Contains($"Icon={Name}", entry, StringComparison.Ordinal);
        Assert.Contains($"StartupWMClass={Name}", entry, StringComparison.Ordinal);
        Assert.Contains("X-VMQontrol-Generated=true", entry, StringComparison.Ordinal);
        foreach (var size in new[] { "16x16", "32x32", "48x48", "128x128", "256x256", "512x512" })
        {
            var icon = File.ReadAllBytes(Path.Combine(home, "icons", "hicolor", size, "apps", Name + ".png"));
            Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], icon[..4]); // a real PNG, not an empty file
        }
    }

    [Fact]
    public void A_moved_build_updates_only_its_own_entry()
    {
        using var profile = new TestProfile("desktop-moved");
        var (home, system, program) = Folders(profile);
        DesktopIntegration.Ensure(home, [system], program);
        var moved = Path.Combine(profile.Root, "elsewhere", "vmqontrol");
        DesktopIntegration.Ensure(home, [system], moved);
        Assert.Contains($"Exec=\"{moved}\" %U", File.ReadAllText(Path.Combine(home, "applications", Name + ".desktop")), StringComparison.Ordinal);
    }

    [Fact]
    public void A_packaged_or_hand_made_entry_is_never_touched()
    {
        using var profile = new TestProfile("desktop-packaged");
        var (home, system, program) = Folders(profile);

        // Installed by a package: nothing is written for the person.
        Directory.CreateDirectory(Path.Combine(system, "applications"));
        File.WriteAllText(Path.Combine(system, "applications", Name + ".desktop"), "[Desktop Entry]\nExec=vmqontrol\n");
        DesktopIntegration.Ensure(home, [system], program);
        Assert.False(Directory.Exists(Path.Combine(home, "applications")));

        // The person's own entry (no marker) stays exactly as they wrote it.
        var own = Path.Combine(home, "applications", Name + ".desktop");
        Directory.CreateDirectory(Path.GetDirectoryName(own)!);
        File.WriteAllText(own, "[Desktop Entry]\nExec=/opt/mine/vmqontrol\n");
        DesktopIntegration.Ensure(home, [Path.Combine(profile.Root, "empty")], program);
        Assert.Equal("[Desktop Entry]\nExec=/opt/mine/vmqontrol\n", File.ReadAllText(own));
    }
}
