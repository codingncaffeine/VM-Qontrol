namespace VmQontrol.Cli;

internal static class Program
{
    public static int Main(string[] args) => Commands.Run(args, Console.Out, Console.Error);
}
