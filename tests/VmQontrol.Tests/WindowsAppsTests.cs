using System.Diagnostics;
using VmQontrol.Core.Install;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Media;
using VmQontrol.Core.Runtime;
using VmQontrol.Core.WindowsApps;

namespace VmQontrol.Tests;

/// <summary>
/// D43 Windows apps as windows: what can be checked without Windows. The guest side was proven on a Windows 11 Pro
/// machine (tools/rdp-proof/prove.sh, s14): RemoteApp windows with no desktop, a Store app and a desktop program in one
/// client, the password kept in a real keyring.
/// </summary>
public sealed class WindowsAppsTests
{
    private static string Pwsh => Path.Combine(TestProfile.ScratchRoot, "tools", "pwsh", "pwsh");

    private static async Task<(int Code, string Said)> PwshAsync(string command, IReadOnlyDictionary<string, string> env)
    {
        var start = new ProcessStartInfo(Pwsh) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", command }) start.ArgumentList.Add(a);
        foreach (var (k, v) in env) start.Environment[k] = v;
        using var p = Process.Start(start)!;
        var said = await p.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken) + await p.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await p.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (p.ExitCode, said);
    }

    private const string ParseFile =
        "$e = $null; [void][System.Management.Automation.Language.Parser]::ParseFile($env:VMQ_PS1, [ref]$null, [ref]$e); $e | ForEach-Object { $_.ToString() }; exit $e.Count";

    public static TheoryData<string> Scripts => ["setup", "undo", "catalog", "catalog-folder", "launcher", "disconnect"];

    private static string ScriptFor(string name) => name switch
    {
        "setup" => WindowsAppsSetup.Script(),
        "undo" => WindowsAppsSetup.Script(undo: true),
        "catalog" => WindowsAppsCatalog.UserScript(),
        "catalog-folder" => WindowsAppsCatalog.PrepareScript("WIN-TEST\\Ada's"),
        "launcher" => WindowsAppsLauncher.InstallScript(),
        "disconnect" => WindowsAppsSession.DisconnectConsoleScript,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    /// <summary>Every script VM Qontrol runs in Windows parses as PowerShell (PowerShell's own parser, scratch/tools/pwsh).</summary>
    [Theory]
    [MemberData(nameof(Scripts))]
    public async Task Every_windows_script_is_valid_powershell(string name)
    {
        Assert.SkipWhen(!File.Exists(Pwsh), "PowerShell is not in scratch/tools/pwsh");
        using var profile = new TestProfile("winapps-ps");
        var file = Path.Combine(profile.Root, name + ".ps1");
        await File.WriteAllTextAsync(file, ScriptFor(name).Replace("\n", "\r\n", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        var (code, said) = await PwshAsync(ParseFile, new Dictionary<string, string> { ["VMQ_PS1"] = file });
        Assert.True(code == 0, said);
    }

    /// <summary>Control: the same check reports a planted fault (a here-string whose end is indented, as a raw string can make it).</summary>
    [Fact]
    public async Task The_powershell_check_sees_a_planted_fault()
    {
        Assert.SkipWhen(!File.Exists(Pwsh), "PowerShell is not in scratch/tools/pwsh");
        using var profile = new TestProfile("winapps-ps-control");
        var file = Path.Combine(profile.Root, "broken.ps1");
        var broken = WindowsAppsLauncher.InstallScript().Replace("\n'@", "\n  '@", StringComparison.Ordinal);
        Assert.NotEqual(WindowsAppsLauncher.InstallScript(), broken);
        await File.WriteAllTextAsync(file, broken, TestContext.Current.CancellationToken);
        var (code, _) = await PwshAsync(ParseFile, new Dictionary<string, string> { ["VMQ_PS1"] = file });
        Assert.NotEqual(0, code);
    }

    /// <summary>
    /// The launcher and the icon helper are C# that Windows compiles with Add-Type; PowerShell compiles them here too
    /// (Roslyn against .NET instead of the .NET Framework compiler, the same language and the same interop attributes).
    /// </summary>
    [Fact]
    public async Task The_csharp_built_inside_windows_compiles_and_a_planted_fault_does_not()
    {
        Assert.SkipWhen(!File.Exists(Pwsh), "PowerShell is not in scratch/tools/pwsh");
        using var profile = new TestProfile("winapps-cs");
        var catalog = WindowsAppsCatalog.UserScript();
        var from = catalog.IndexOf("@'", StringComparison.Ordinal) + 2;
        var icon = catalog[from..catalog.IndexOf("\n'@", StringComparison.Ordinal)];
        var cases = new[] { ("launcher", WindowsAppsLauncher.Source, ""), ("icon", icon, "-ReferencedAssemblies System.Drawing.Common,System.Drawing.Primitives,System.Private.Windows.Core,System.Private.Windows.GdiPlus "),
            ("control", WindowsAppsLauncher.Source.Replace("return 0;", "return 0", StringComparison.Ordinal), "") };
        foreach (var (name, source, refs) in cases)
        {
            var file = Path.Combine(profile.Root, name + ".cs");
            await File.WriteAllTextAsync(file, source, TestContext.Current.CancellationToken);
            var (code, said) = await PwshAsync($"Add-Type {refs}-TypeDefinition (Get-Content -Raw $env:VMQ_CS)", new Dictionary<string, string> { ["VMQ_CS"] = file });
            if (name == "control") Assert.True(code != 0, "the planted fault compiled");
            else Assert.True(code == 0, name + ": " + said);
        }
    }

    [Fact]
    public void Home_editions_are_refused_in_words_and_pro_is_ready()
    {
        Assert.True(WindowsAppsSetup.IsHome("Core"));
        Assert.True(WindowsAppsSetup.IsHome("CoreSingleLanguage"));
        Assert.False(WindowsAppsSetup.IsHome("Professional"));
        Assert.False(WindowsAppsSetup.IsHome("Enterprise"));

        var home = WindowsAppsSetup.Parse(new GuestAgentClient.ExecResult(0, "edition=Core\r\nproduct=Windows 10 Home\r\nbuild=26100\r\nresult=home\r\n", "", false));
        Assert.False(home.Ready);
        Assert.Contains("Windows 11 Home is a Home edition", home.Why, StringComparison.Ordinal);

        var pro = WindowsAppsSetup.Parse(new GuestAgentClient.ExecResult(0,
            "edition=Professional\nproduct=Windows 10 Pro\nbuild=19045\ncert=c595b161\nsignedin=WIN-TEST\\tester\nsource=Local\nresult=ok\n", "", false));
        Assert.True(pro.Ready, pro.Why);
        Assert.Equal("Windows 10 Pro", pro.Product);
        Assert.Equal("c595b161", pro.CertificateSha256);
        Assert.Equal("tester", WindowsAppsClient.AccountName(pro.SignedIn!));

        Assert.False(WindowsAppsSetup.Parse(null).Ready);
        Assert.Contains("two minutes", WindowsAppsSetup.Parse(new GuestAgentClient.ExecResult(null, "", "", true)).Why, StringComparison.Ordinal);
        Assert.Contains("Access denied", WindowsAppsSetup.Parse(new GuestAgentClient.ExecResult(0, "error=Access denied\nresult=failed", "", false)).Why, StringComparison.Ordinal);
    }

    [Fact]
    public void The_catalog_reads_powershells_json_and_keeps_only_icon_file_names()
    {
        const string json = "\uFEFF[{\"id\":\"Microsoft.Paint_8wekyb3d8bbwe!App\",\"name\":\"Paint\",\"program\":null,\"arguments\":null,\"icon\":\"ef88144d7669.png\"},"
            + "{\"id\":\"MSEdge\",\"name\":\"Microsoft Edge\",\"program\":\"C:\\\\Program Files (x86)\\\\Microsoft\\\\Edge\\\\Application\\\\msedge.exe\",\"arguments\":\"--profile-directory=Default\",\"icon\":\"..\\\\..\\\\evil.png\"},"
            + "{\"id\":\"\",\"name\":\"nameless\"}]";
        var apps = WindowsAppsCatalog.Parse(json);
        Assert.Equal(2, apps.Count);
        Assert.True(apps[0].Packaged);
        Assert.Equal("ef88144d7669.png", apps[0].Icon);
        Assert.Null(apps[1].Icon); // a path from the guest is never used as a file name here
        Assert.Equal("\"C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe\" --profile-directory=Default", apps[1].CommandLine);
        Assert.Equal(WindowsAppsCatalog.Slug("msedge"), apps[1].Slug);
    }

    [Fact]
    public void FreeRDP_gets_a_pinned_certificate_and_never_explorer()
    {
        var store = new WindowsApp("Microsoft.Paint_8wekyb3d8bbwe!App", "Paint, the app", null, null);
        var desktop = new WindowsApp("x", "Character Map", @"C:\Windows\System32\charmap.exe", null);
        var args = WindowsAppsClient.Arguments(new RdpRoute("/run/user/1000/vmq/rdp.sock", true), "tester", "pw", "ab12", store, "vm-qontrol-5a1e");
        Assert.Contains("/v:/run/user/1000/vmq/rdp.sock", args);
        Assert.Contains("/cert:fingerprint:sha256:ab12", args);
        Assert.DoesNotContain(args, a => a.Contains("ignore", StringComparison.Ordinal) || a.Contains("tofu", StringComparison.Ordinal));
        Assert.Contains("/auth-pkg-list:!kerberos", args);
        Assert.Contains($"/app:program:{WindowsAppsLauncher.Exe},cmd:activate-here Microsoft.Paint_8wekyb3d8bbwe!App,name:Paint  the app", args);
        Assert.DoesNotContain(args, a => a.Contains("explorer", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(@"/app:program:C:\Windows\System32\charmap.exe,name:Character Map", WindowsAppsClient.AppOption(desktop));
    }

    [Fact]
    public void Sound_is_asked_for_only_when_there_is_a_sound_server()
    {
        var before = Environment.GetEnvironmentVariable("PULSE_SERVER");
        try
        {
            Environment.SetEnvironmentVariable("PULSE_SERVER", "unix:/nonexistent/vmq");
            Assert.False(WindowsAppsClient.SoundServer());
            Assert.DoesNotContain(WindowsAppsClient.Arguments(new RdpRoute("h:1", false), "a", "b", "c", new WindowsApp("x", "y", "z.exe", null), "k"), a => a.StartsWith("/sound", StringComparison.Ordinal));
            Environment.SetEnvironmentVariable("PULSE_SERVER", "tcp:host:4713");
            Assert.True(WindowsAppsClient.SoundServer());
        }
        finally
        {
            Environment.SetEnvironmentVariable("PULSE_SERVER", before);
        }
    }

    /// <summary>An Easy Install machine signs in with the password on its own answer disc, read from the real disc format.</summary>
    [Fact]
    public void An_easy_install_machine_brings_its_own_password()
    {
        using var profile = new TestProfile("winapps-answer");
        var xml = WindowsAnswerFile.Build(new WindowsInstallChoices { UserName = "tester", Password = "Qontrol-1 & <x>", ComputerName = "WIN-TEST" }, "11");
        IsoWriter.Write(Path.Combine(profile.Root, "unattended.iso"), "UNATTEND", new Dictionary<string, byte[]> { ["autounattend.xml"] = System.Text.Encoding.UTF8.GetBytes(xml) });
        var config = new MachineConfig { Name = "w", EasyInstall = new EasyInstallConfig { UserName = "tester" } };
        Assert.Equal("Qontrol-1 & <x>", WindowsAppsClient.EasyInstallPassword(config, profile.Root, "tester"));
        Assert.Equal("Qontrol-1 & <x>", WindowsAppsClient.EasyInstallPassword(config, profile.Root, "TESTER"));
        Assert.Null(WindowsAppsClient.EasyInstallPassword(config, profile.Root, "someone-else"));
        Assert.Null(WindowsAppsClient.EasyInstallPassword(config with { EasyInstall = null }, profile.Root, "tester"));
    }

    [Fact]
    public void Windows_11_is_named_from_its_build()
    {
        Assert.Equal("Windows 11 Pro", WindowsAppsSetup.ProductName("Windows 10 Pro", "26100"));
        Assert.Equal("Windows 10 Pro", WindowsAppsSetup.ProductName("Windows 10 Pro", "19045"));
        Assert.Equal("Windows Server 2022 Standard", WindowsAppsSetup.ProductName("Windows Server 2022 Standard", "20348"));
    }
}
