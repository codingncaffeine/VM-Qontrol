using System.Text;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Core.WindowsApps;

/// <summary>What Windows said when it was asked to let its programs out as windows (D43).</summary>
public sealed record WindowsAppsReadiness(
    bool Ready,
    string? Why,
    string? Edition,
    string? Product,
    string? CertificateSha256,
    string? SignedIn,
    string? AccountSource);

/// <summary>
/// D43: Windows programs as their own Linux windows through RDP RemoteApp. The guest side is set up by our guest agent,
/// as SYSTEM, in one idempotent PowerShell script: Remote Desktop on, its firewall group on (by the group's
/// locale-neutral resource name, so a German or Japanese Windows works too), programs not on the RemoteApp list allowed,
/// network-level authentication kept on, and the RDP certificate's SHA-256 read back so the client can PIN it. Home
/// editions have no Remote Desktop server; they are refused in words, never patched (RDP Wrapper edits termsrv.dll).
/// </summary>
public static class WindowsAppsSetup
{
    /// <summary>Remote Desktop's firewall group, by resource name (the display name is translated).</summary>
    public const string FirewallGroup = "@FirewallAPI.dll,-28752";

    /// <summary>Editions with no Remote Desktop server (EditionID values Windows uses for Home).</summary>
    public static bool IsHome(string? editionId) =>
        editionId is { } e && (e.StartsWith("Core", StringComparison.OrdinalIgnoreCase) || e.Equals("Home", StringComparison.OrdinalIgnoreCase));

    /// <summary>The setup script (PowerShell 5.1, run as SYSTEM). Prints key=value lines; the last <c>result=</c> decides.</summary>
    public static string Script(bool undo = false)
    {
        var body = undo
            ? """
              Set-ItemProperty $ts fDenyTSConnections 1 -Type DWord
              Disable-NetFirewallRule -Group $group -ErrorAction SilentlyContinue
              if (Test-Path $allow) { Remove-ItemProperty $allow fDisabledAllowList -ErrorAction SilentlyContinue }
              if (Test-Path $policy) { Remove-ItemProperty $policy fAllowUnlistedRemotePrograms -ErrorAction SilentlyContinue }
              'result=ok'
              """
            : """
              Set-ItemProperty $ts fDenyTSConnections 0 -Type DWord
              # Network-level authentication stays on: the password is checked before Windows draws anything.
              Set-ItemProperty "$ts\WinStations\RDP-Tcp" UserAuthentication 1 -Type DWord
              Enable-NetFirewallRule -Group $group -ErrorAction SilentlyContinue
              # RemoteApp of any program, not only the ones on the server's list (client editions have no list editor).
              if (-not (Test-Path $allow)) { New-Item $allow | Out-Null }
              Set-ItemProperty $allow fDisabledAllowList 1 -Type DWord
              if (-not (Test-Path $policy)) { New-Item $policy | Out-Null }
              Set-ItemProperty $policy fAllowUnlistedRemotePrograms 1 -Type DWord
              Set-Service TermService -StartupType Manual -ErrorAction SilentlyContinue
              Start-Service TermService -ErrorAction SilentlyContinue
              # The listener's certificate; created when the service first listens.
              $sha = $null
              for ($i = 0; $i -lt 20 -and -not $sha; $i++) {
                  $hash = (Get-CimInstance -Namespace root\cimv2\TerminalServices -ClassName Win32_TSGeneralSetting -Filter "TerminalName='RDP-tcp'" -ErrorAction SilentlyContinue).SSLCertificateSHA1Hash
                  if ($hash) {
                      $cert = Get-Item "Cert:\LocalMachine\Remote Desktop\$hash" -ErrorAction SilentlyContinue
                      if ($cert) { $sha = -join ([Security.Cryptography.SHA256]::Create().ComputeHash($cert.RawData) | ForEach-Object { $_.ToString('x2') }) }
                  }
                  if (-not $sha) { Start-Sleep -Milliseconds 500 }
              }
              if ($sha) { "cert=$sha" }
              $who = (Get-CimInstance Win32_ComputerSystem).UserName
              if ($who) {
                  "signedin=$who"
                  $local = Get-LocalUser -Name ($who -split '\\')[-1] -ErrorAction SilentlyContinue
                  if ($local) { "source=$($local.PrincipalSource)" }
              }
              if ($sha) { 'result=ok' } else { 'result=no-cert' }
              """;
        return $$"""
            $ErrorActionPreference = 'Stop'
            $cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
            "edition=$($cv.EditionID)"
            "product=$($cv.ProductName)"
            "build=$($cv.CurrentBuild)"
            $ts = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server'
            $allow = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Terminal Server\TSAppAllowList'
            $policy = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services'
            $group = '{{FirewallGroup}}'
            if ('{{(undo ? "undo" : "do")}}' -eq 'do' -and ($cv.EditionID -like 'Core*' -or $cv.EditionID -eq 'Home')) { 'result=home'; exit 0 }
            try {
            {{body}}
            } catch { "error=$($_.Exception.Message)"; 'result=failed' }
            """;
    }

    /// <summary>Reads the script's key=value lines.</summary>
    public static WindowsAppsReadiness Parse(GuestAgentClient.ExecResult? result)
    {
        if (result is null) return new(false, "Windows' guest agent did not answer; it comes with the guest tools.", null, null, null, null, null);
        if (result.TimedOut) return new(false, "Windows did not finish the setup within two minutes.", null, null, null, null, null);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (line.IndexOf('=', StringComparison.Ordinal) is var eq and > 0) values[line[..eq]] = line[(eq + 1)..];
        string? Get(string k) => values.TryGetValue(k, out var v) && v.Length > 0 ? v : null;
        var edition = Get("edition");
        var product = ProductName(Get("product"), Get("build"));
        var why = Get("result") switch
        {
            "ok" => null,
            "home" => $"{product ?? "This Windows"} is a Home edition, which has no Remote Desktop server, so its programs cannot open as windows here. Pro, Enterprise and Education can.",
            "no-cert" => "Remote Desktop was turned on, but Windows did not make its certificate; restart Windows and try again.",
            _ => $"The setup did not finish ({Get("error") ?? (result.Errors + result.Output).Trim()}).",
        };
        return new(why is null, why, edition, product, Get("cert"), Get("signedin"), Get("source"));
    }

    /// <summary>
    /// Windows 11 still says "Windows 10" in its ProductName (Microsoft never changed the value); build 22000 and later is
    /// Windows 11.
    /// </summary>
    public static string? ProductName(string? product, string? build) =>
        product is not null && int.TryParse(build, out var b) && b >= 22000 && product.StartsWith("Windows 10", StringComparison.Ordinal)
            ? "Windows 11" + product[10..]
            : product;

    /// <summary>Runs the setup (or its undo) inside the guest.</summary>
    public static async Task<WindowsAppsReadiness> PrepareAsync(GuestAgentClient agent, CancellationToken ct, bool undo = false)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script(undo)));
        var result = await agent.ExecAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded],
            null, TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        return Parse(result);
    }
}

/// <summary>Starting programs inside the session the machine's RDP client holds.</summary>
public static class WindowsAppsSession
{
    internal static Task<VmQontrol.Core.Runtime.GuestAgentClient.ExecResult?> PowerShellAsync(VmQontrol.Core.Runtime.GuestAgentClient agent, string script, TimeSpan runFor, CancellationToken ct) =>
        agent.ExecAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], null, runFor, ct);

    /// <summary>Compiles the launcher into the guest when it is missing; null when it is there, else why not.</summary>
    public static async Task<string?> EnsureLauncherAsync(VmQontrol.Core.Runtime.GuestAgentClient agent, CancellationToken ct)
    {
        var r = await PowerShellAsync(agent, WindowsAppsLauncher.InstallScript(), TimeSpan.FromMinutes(1), ct).ConfigureAwait(false);
        return r is not null && r.Output.Contains("result=ok", StringComparison.Ordinal) ? null
            : $"Windows could not build VM Qontrol's program starter ({(r is null ? agent.LastError ?? "no answer" : (r.Errors + r.Output).Trim())}).";
    }

    /// <summary>The id of the session an RDP client holds, or null when no client is connected.</summary>
    public static async Task<int?> ActiveSessionAsync(VmQontrol.Core.Runtime.GuestAgentClient agent, CancellationToken ct)
    {
        var r = await agent.ExecAsync(WindowsAppsLauncher.Exe, ["session"], null, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        var line = r?.Output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("session=", StringComparison.Ordinal));
        return line is not null && int.TryParse(line[8..], out var id) ? id : null;
    }

    /// <summary>Starts <paramref name="app"/> in the RDP session as its user; null when started, else why not.</summary>
    public static Task<string?> RunAsync(VmQontrol.Core.Runtime.GuestAgentClient agent, WindowsApp app, CancellationToken ct) =>
        app.Packaged ? StartAsync(agent, ["activate", app.Id], ct) : StartAsync(agent, ["run", app.CommandLine, ""], ct);

    /// <summary>Starts a command line in the RDP session as its user; null when started, else why not.</summary>
    public static Task<string?> RunAsync(VmQontrol.Core.Runtime.GuestAgentClient agent, string commandLine, string? workingFolder, CancellationToken ct) =>
        StartAsync(agent, ["run", commandLine, workingFolder ?? ""], ct);

    private static async Task<string?> StartAsync(VmQontrol.Core.Runtime.GuestAgentClient agent, string[] arguments, CancellationToken ct)
    {
        var r = await agent.ExecAsync(WindowsAppsLauncher.Exe, arguments, null, TimeSpan.FromSeconds(45), ct).ConfigureAwait(false);
        if (r is null) return $"Windows' guest agent did not answer ({agent.LastError ?? "no answer"}).";
        var result = r.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("result=", StringComparison.Ordinal)) ?? "";
        return result switch
        {
            "result=ok" => null,
            "result=no-session" => "No Windows apps session is open.",
            _ => $"Windows did not start it ({result[Math.Min(7, result.Length)..]}).",
        };
    }

    /// <summary>
    /// Disconnects the session on Windows' own screen (programs keep running, nothing is signed out), so an RDP sign-in
    /// takes the one interactive session without Windows asking "Another user is signed in" on a screen nobody sees.
    /// </summary>
    public static async Task DisconnectConsoleAsync(VmQontrol.Core.Runtime.GuestAgentClient agent, CancellationToken ct) =>
        await PowerShellAsync(agent, DisconnectConsoleScript, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);

    /// <summary>The console's session id from query session (its name column is "console" in every language), then tsdiscon.</summary>
    public const string DisconnectConsoleScript = """
        $line = (query session) | Where-Object { $_ -match '^[ >]console\s+\S+\s+(\d+)\s+Active' } | Select-Object -First 1
        if ($line -and $line -match '^[ >]console\s+\S+\s+(\d+)\s+Active') { tsdiscon $Matches[1] }
        'result=ok'
        """;
}
