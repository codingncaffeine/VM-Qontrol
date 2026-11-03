using System.Diagnostics;
using VmQontrol.Core.Hosts;

namespace VmQontrol.Tests;

/// <summary>
/// D38: the scripts the password prompt runs, run here without root inside bwrap: a scratch tmpfs over /etc/systemd and a
/// stand-in systemctl that only writes down what it was asked (so no real logind is reloaded and no prompt appears).
/// </summary>
public sealed class ShutdownTimeTests
{
    [Fact]
    public void Raising_writes_the_drop_in_and_reloads_logind_and_putting_back_removes_it()
    {
        Assert.SkipWhen(!File.Exists("/usr/bin/bwrap"), "bwrap is not installed");
        using var profile = new TestProfile("shutdown-time");
        var stub = Path.Combine(profile.Root, "bin");
        Directory.CreateDirectory(stub);
        var asked = Path.Combine(profile.Root, "asked");
        File.WriteAllText(Path.Combine(stub, "systemctl"), $"#!/bin/sh\necho \"$@\" >> '{asked}'\n");
        File.SetUnixFileMode(Path.Combine(stub, "systemctl"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        string Run(string script)
        {
            var start = new ProcessStartInfo("/usr/bin/bwrap") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "--dev-bind", "/", "/", "--tmpfs", "/etc/systemd", "--setenv", "PATH", $"{stub}:/usr/bin:/bin", "/bin/sh", "-c",
                         $"{script}; echo \"exit=$?\"; cat {ShutdownTime.DropIn} 2>/dev/null || echo NO-DROP-IN" })
                start.ArgumentList.Add(a);
            using var p = Process.Start(start)!;
            var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            return output;
        }

        var probe = Run("true");
        Assert.SkipWhen(!probe.Contains("exit=0", StringComparison.Ordinal), $"bwrap cannot make a namespace here: {probe.Trim()}");

        var raised = Run(ShutdownTime.RaiseScript);
        Assert.Contains("exit=0", raised, StringComparison.Ordinal);
        Assert.Contains("[Login]\nInhibitDelayMaxSec=120", raised, StringComparison.Ordinal);
        Assert.Equal("reload systemd-logind", File.ReadAllText(asked).Trim());

        File.Delete(asked);
        var restored = Run($"{ShutdownTime.RaiseScript} >/dev/null; {ShutdownTime.RestoreScript}");
        Assert.Contains("exit=0", restored, StringComparison.Ordinal);
        Assert.Contains("NO-DROP-IN", restored, StringComparison.Ordinal);
        Assert.Equal(["reload systemd-logind", "reload systemd-logind"], File.ReadAllLines(asked));
    }

    [Fact]
    public void The_answer_says_whether_logind_took_the_new_time()
    {
        Assert.StartsWith("Machines now get up to 2 minutes", ShutdownTime.Outcome(true, TimeSpan.FromSeconds(120)).Message, StringComparison.Ordinal);
        Assert.StartsWith("Saved. This computer still gives 5 s until it restarts", ShutdownTime.Outcome(true, TimeSpan.FromSeconds(5)).Message, StringComparison.Ordinal);
        Assert.Equal("Back to this computer's own time (5 s).", ShutdownTime.Outcome(false, TimeSpan.FromSeconds(5)).Message);
        Assert.Equal("Back to this computer's own time (120 s until it restarts).", ShutdownTime.Outcome(false, TimeSpan.FromSeconds(120)).Message);
    }
}
