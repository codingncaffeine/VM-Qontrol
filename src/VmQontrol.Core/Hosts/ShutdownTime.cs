using System.Diagnostics;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Core.Hosts;

/// <summary>
/// The time logind gives programs before shutdown (InhibitDelayMaxSec, 5 s by default), and raising it so the shutdown
/// guard can save large machines, as libvirt-guests and Hyper-V wait for theirs (D38). One polkit prompt writes a logind
/// drop-in and asks logind to reload; logind is never restarted. The value is read back from logind itself, so the
/// answer says whether it applies now or after this computer restarts. Putting it back removes the drop-in.
/// </summary>
public static class ShutdownTime
{
    public const int RaisedSeconds = 120;
    public const string DropIn = "/etc/systemd/logind.conf.d/50-vmqontrol.conf";

    /// <summary>elogind (Alpine, Devuan, Gentoo without systemd) reads its drop-ins here.</summary>
    public const string ElogindDropIn = "/etc/elogind/logind.conf.d/50-vmqontrol.conf";

    public static bool IsRaised => File.Exists(DropIn) || File.Exists(ElogindDropIn);

    /// <summary>The script the polkit prompt runs to raise it: the drop-in for systemd's logind (and elogind's, where present), then a reload.</summary>
    public static string RaiseScript =>
        $"mkdir -p /etc/systemd/logind.conf.d && printf '[Login]\\nInhibitDelayMaxSec={RaisedSeconds}\\n' > {DropIn} && " +
        $"(if [ -d /etc/elogind ]; then mkdir -p /etc/elogind/logind.conf.d && cp {DropIn} {ElogindDropIn}; fi; " +
        "systemctl reload systemd-logind 2>/dev/null; true)";

    public static string RestoreScript => $"rm -f {DropIn} {ElogindDropIn} && (systemctl reload systemd-logind 2>/dev/null; true)";

    /// <summary>Raises or restores it (the password is asked for); returns what happened in words.</summary>
    public static async Task<(bool Done, string Message)> SetAsync(bool raise, CancellationToken ct = default)
    {
        if (!File.Exists("/usr/bin/pkexec")) return (false, "polkit's pkexec is not installed, so the password cannot be asked for.");
        var start = new ProcessStartInfo("/usr/bin/pkexec") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in (string[])["/bin/sh", "-c", raise ? RaiseScript : RestoreScript]) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        var error = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (p.ExitCode is 126 or 127) return (false, p.ExitCode == 126 ? "The password prompt was closed." : "polkit refused.");
        if (p.ExitCode != 0) return (false, error.Trim());
        TimeSpan now;
        try
        {
            now = await Logind.ForThisComputer().DelayBudgetAsync().WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or Tmds.DBus.Protocol.DBusConnectionException or Tmds.DBus.Protocol.DBusErrorReplyException)
        {
            return (true, "Saved; logind could not be asked whether it applies yet.");
        }

        return Outcome(raise, now);
    }

    /// <summary>What to say once logind reports <paramref name="now"/>.</summary>
    public static (bool Done, string Message) Outcome(bool raise, TimeSpan now)
    {
        if (raise)
        {
            return now.TotalSeconds >= RaisedSeconds
                ? (true, $"Machines now get up to {RaisedSeconds / 60} minutes to save. Shutdown waits only as long as saving takes.")
                : (true, $"Saved. This computer still gives {now.TotalSeconds:0.#} s until it restarts; after that, up to {RaisedSeconds / 60} minutes.");
        }

        return (true, $"Back to this computer's own time ({now.TotalSeconds:0.#} s{(now.TotalSeconds >= RaisedSeconds ? " until it restarts" : "")}).");
    }
}
