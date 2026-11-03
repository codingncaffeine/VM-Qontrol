using System.Diagnostics;
using System.Reflection;

namespace VmQontrol.Core.Runtime;

/// <summary>
/// Starts one of VM Qontrol's own helpers (a segment's address server, the shutdown guard) as a process of its own, so it
/// outlives the program that started it, as the machines do.
/// </summary>
internal static class Detached
{
    /// <summary>Whether this process is one of VM Qontrol's programs (vmq or vmqontrol), so it can start itself again.</summary>
    public static bool CanStartSelf
    {
        get
        {
            var program = Environment.ProcessPath ?? "";
            var entry = Assembly.GetEntryAssembly()?.Location ?? "";
            return Path.GetFileName(program) is "vmq" or "vmqontrol" || Path.GetFileNameWithoutExtension(entry) is "vmq" or "VmQontrol.App" or "vmqontrol";
        }
    }

    /// <summary>
    /// This program again with <paramref name="args"/>, in a session of its own, its output to /dev/null: that program's
    /// terminal or pipe closing must not end it (its log file is where it writes). Returns false when it could not start.
    /// </summary>
    public static bool StartSelf(IEnumerable<string> args, out string? failure)
    {
        var program = Environment.ProcessPath ?? "";
        var entry = Assembly.GetEntryAssembly()?.Location ?? "";
        var start = new ProcessStartInfo("setsid") { UseShellExecute = false };
        start.ArgumentList.Add("sh");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("exec \"$0\" \"$@\" < /dev/null > /dev/null 2>&1");
        start.ArgumentList.Add(program);
        if (Path.GetFileName(program) is "dotnet") start.ArgumentList.Add(entry);
        foreach (var a in args) start.ArgumentList.Add(a);
        try
        {
            using var _ = Process.Start(start);
            failure = null;
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            failure = ex.Message;
            return false;
        }
    }
}
