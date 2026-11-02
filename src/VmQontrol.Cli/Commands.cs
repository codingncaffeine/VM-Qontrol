using VmQontrol.Core;

namespace VmQontrol.Cli;

/// <summary>The <c>vmq</c> commands. Output goes to the writers passed in, so tests read exactly what a user sees.</summary>
internal static class Commands
{
    public const string Usage = """
        Usage: vmq <command> [options]

          version             Print the version and licence.
          help                Show this help.

        VMQONTROL_HOME=DIR keeps every setting, log and machine under DIR instead of the usual folders.
        """;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        var command = args.Length == 0 ? "help" : args[0];
        switch (command)
        {
            case "version" or "--version" or "-V":
                output.WriteLine($"vmq {BuildInfo.Stamp}");
                output.WriteLine(BuildInfo.Licence);
                return 0;
            case "help" or "--help" or "-h":
                output.WriteLine(Usage);
                return 0;
            default:
                error.WriteLine($"vmq: unknown command '{command}'. Run 'vmq help' for the list.");
                return 2;
        }
    }
}
