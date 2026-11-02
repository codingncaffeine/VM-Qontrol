using VmQontrol.Core;

namespace VmQontrol.Tests;

public sealed class PathsTests
{
    [Fact]
    public void The_home_variable_puts_every_folder_library_included_under_it()
    {
        var paths = QontrolPaths.FromEnvironment(v => v switch { QontrolPaths.HomeVariable => "/x/home", "HOME" => "/home/u", _ => null });
        Assert.True(paths.Isolated);
        foreach (var dir in new[] { paths.Config, paths.Data, paths.State, paths.Cache, paths.DefaultLibrary, paths.Logs })
            Assert.StartsWith("/x/home/", dir, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_it_the_XDG_folders_are_used_and_the_library_is_quickemus_VMs_folder()
    {
        var paths = QontrolPaths.FromEnvironment(v => v switch { "HOME" => "/home/u", "XDG_CONFIG_HOME" => "/cfg", _ => null });
        Assert.False(paths.Isolated);
        Assert.Equal("/cfg/vmqontrol", paths.Config);
        Assert.Equal("/home/u/.local/share/vmqontrol", paths.Data);
        Assert.Equal("/home/u/.local/state/vmqontrol", paths.State);
        Assert.Equal("/home/u/VMs", paths.DefaultLibrary);
    }

    [Fact]
    public void A_relative_XDG_folder_is_ignored_as_the_specification_says()
    {
        var paths = QontrolPaths.FromEnvironment(v => v switch { "HOME" => "/home/u", "XDG_CONFIG_HOME" => "relative/dir", _ => null });
        Assert.Equal("/home/u/.config/vmqontrol", paths.Config);
    }
}
