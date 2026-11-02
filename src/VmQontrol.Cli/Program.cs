namespace VmQontrol.Cli;

internal static class Program
{
    public static int Main(string[] args)
    {
        // vmq's own output says what happened; the log's info lines would only repeat it on the terminal.
        Core.Log.Minimum = Core.LogLevel.Warning;
        return Commands.Run(args, Console.Out, Console.Error);
    }
}
