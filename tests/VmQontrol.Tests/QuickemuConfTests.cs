using VmQontrol.Core.Import;

namespace VmQontrol.Tests;

public sealed class QuickemuConfTests
{
    [Fact]
    public void Reads_quoted_unquoted_and_array_settings_and_skips_comments()
    {
        var conf = QuickemuConf.Parse("""
            #!/usr/bin/quickemu --vm
            # a comment
            guest_os="windows"
            disk_size=64G   # trailing comment
            iso='my iso/win.iso'
            port_forwards=("8080:80"
                           "2222:22")
            usb_devices=("046d:c52b")
            echo hello
            """);
        Assert.Equal("windows", conf["guest_os"]);
        Assert.Equal("64G", conf["disk_size"]);
        Assert.Equal("my iso/win.iso", conf["iso"]);
        Assert.Equal(["8080:80", "2222:22"], conf.Array("port_forwards"));
        Assert.Equal(["046d:c52b"], conf.Array("usb_devices"));
        Assert.Equal(["echo hello"], conf.Unread);
    }

    [Fact]
    public void An_empty_value_reads_as_unset()
    {
        var conf = QuickemuConf.Parse("ram=\"\"\n");
        Assert.Null(conf["ram"]);
    }

    [Theory]
    [InlineData("8G", 8192L)]
    [InlineData("4096M", 4096L)]
    [InlineData("16", 16384L)]
    [InlineData("1T", 1048576L)]
    public void Sizes_read_as_quickemu_writes_them(string text, long mib) =>
        Assert.Equal(mib, QuickemuImporter.MemoryMiB(text));
}
