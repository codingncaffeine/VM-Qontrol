using VmQontrol.Cli;

namespace VmQontrol.Tests;

public sealed class CliTests
{
    private static (int Code, string Out, string Err) Vmq(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = Commands.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void Version_names_the_tool_and_the_licence()
    {
        var (code, output, _) = Vmq("version");
        Assert.Equal(0, code);
        Assert.StartsWith("vmq ", output, StringComparison.Ordinal);
        Assert.Contains("GPLv3", output, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_command_fails_with_a_pointer_to_help()
    {
        var (code, _, error) = Vmq("frobnicate");
        Assert.Equal(2, code);
        Assert.Contains("vmq help", error, StringComparison.Ordinal);
    }
}
