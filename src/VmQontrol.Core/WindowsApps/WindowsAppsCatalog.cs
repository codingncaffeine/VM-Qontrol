using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Core.WindowsApps;

/// <summary>One program Windows lists in its Start menu.</summary>
/// <param name="Id">Windows' own app id (the Start menu's AppID): a path, a known-folder path or a Store app's AUMID.</param>
/// <param name="Name">The name Start shows.</param>
/// <param name="Program">The .exe to start, or <c>null</c> when Windows names it only by its id (started through shell:AppsFolder).</param>
/// <param name="Icon">The icon's file name in the machine's catalog folder, or <c>null</c> when Windows gave none.</param>
/// <param name="Arguments">The shortcut's arguments for the program, if any.</param>
public sealed record WindowsApp(string Id, string Name, string? Program, string? Icon, string? Arguments = null)
{
    /// <summary>A packaged (Store) app, started by its app id through Windows' activation manager.</summary>
    public bool Packaged => Program is null;

    /// <summary>The command line that starts a desktop program inside Windows.</summary>
    public string CommandLine => WindowsAppsLauncher.CommandLine(Program ?? "", Arguments);

    /// <summary>A short stable name for files and desktop entries.</summary>
    public string Slug => WindowsAppsCatalog.Slug(Id);
}

/// <summary>
/// D43 step 20d: the programs Windows' Start menu shows, with their icons. Asked of Windows as the signed-in user (Store
/// apps and per-account shortcuts are not visible to SYSTEM): the launcher runs the script hidden in that user's session.
/// Get-StartApps is the Start menu's own list (names as the user sees them, duplicates already folded); icons come from
/// the shell's image factory at 256 px on shell:AppsFolder\ID, the same picture Start draws, for desktop and Store apps.
/// The list and icons are written to a folder the user may write in and read back by the agent.
/// </summary>
public static partial class WindowsAppsCatalog
{
    /// <summary>The only icon names taken from Windows: the ones the script writes (a list from the guest is untrusted).</summary>
    [System.Text.RegularExpressions.GeneratedRegex("^[0-9a-f]{12}\\.png$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex IconName();

    public const string GuestFolder = SharedFolders.WindowsFolder + @"\apps";

    public static string Slug(string id) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToLowerInvariant())))[..12];

    /// <summary>The script the user's session runs (PowerShell 5.1).</summary>
    public static string UserScript() => $$"""
        $ErrorActionPreference = 'Continue'
        $out = '{{GuestFolder}}'
        Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
        using System;
        using System.Drawing;
        using System.Drawing.Imaging;
        using System.Runtime.InteropServices;
        public static class VmqIcon
        {
            [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            interface IShellItemImageFactory { [PreserveSig] int GetImage(Size size, int flags, out IntPtr bitmap); }
            [StructLayout(LayoutKind.Sequential)] struct Size { public int X, Y; }
            [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
            static extern void SHCreateItemFromParsingName(string name, IntPtr bind, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);
            [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);
            [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, int flags, IntPtr token, out string path);

            public static string Folder(string guid)
            {
                string path;
                return SHGetKnownFolderPath(new Guid(guid), 0, IntPtr.Zero, out path) == 0 ? path : null;
            }

            // The shell's picture of NAME at PX pixels, alpha kept (Image.FromHbitmap drops it), as a PNG.
            public static bool Save(string name, string file, int px)
            {
                IShellItemImageFactory factory;
                try { SHCreateItemFromParsingName(name, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory); }
                catch { return false; }
                IntPtr hbitmap;
                // SIIGBF_ICONONLY: the icon, never a thumbnail of the file.
                if (factory.GetImage(new Size { X = px, Y = px }, 0x4, out hbitmap) != 0) return false;
                try
                {
                    using (var source = Image.FromHbitmap(hbitmap))
                    {
                        var rect = new Rectangle(0, 0, source.Width, source.Height);
                        var data = source.LockBits(rect, ImageLockMode.ReadOnly, source.PixelFormat);
                        try
                        {
                            using (var alpha = new Bitmap(data.Width, data.Height, data.Stride, PixelFormat.Format32bppPArgb, data.Scan0))
                            using (var copy = new Bitmap(alpha))
                                copy.Save(file, ImageFormat.Png);
                        }
                        finally { source.UnlockBits(data); }
                    }
                    return true;
                }
                finally { DeleteObject(hbitmap); }
            }
        }
        '@
        $targets = @{}
        foreach ($item in (New-Object -ComObject Shell.Application).NameSpace('shell:AppsFolder').Items()) { $targets[$item.Path] = $item }
        $apps = @()
        foreach ($app in (Get-StartApps | Sort-Object Name)) {
            $id = $app.AppID
            if (-not $id -or $app.Name -match '(?i)uninstall|readme|release notes|documentation|website') { continue }
            $program = $null
            $arguments = $null
            if ($id -match '^\{([0-9A-Fa-f-]{36})\}\\(.+)$') {
                $root = [VmqIcon]::Folder($Matches[1])
                if ($root) { $program = Join-Path $root $Matches[2] }
            } elseif ($id -match '^[A-Za-z]:\\') { $program = $id }
            elseif (-not ($id -match '!')) {
                # A desktop program Windows names by id only (Edge, shortcuts with their own id): its shortcut's target.
                $item = $targets[$id]
                if ($item) {
                    $program = $item.ExtendedProperty('System.Link.TargetParsingPath')
                    $arguments = $item.ExtendedProperty('System.Link.Arguments')
                }
                if (-not $program) { continue }   # a shell command (Run, Control Panel pages) with no program to start
            }
            if ($program -and -not ($program -match '(?i)\.exe$')) { continue }   # help files, links to web pages
            if ($program -and -not (Test-Path -LiteralPath $program)) { continue }
            $sha = [Security.Cryptography.SHA256]::Create()
            $slug = (-join ($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($id.ToLowerInvariant())) | ForEach-Object { $_.ToString('x2') })).Substring(0, 12)
            $icon = $null
            if ([VmqIcon]::Save("shell:AppsFolder\$id", "$out\$slug.png", 256)) { $icon = "$slug.png" }
            $apps += [pscustomobject]@{ id = $id; name = $app.Name; program = $program; arguments = $arguments; icon = $icon }
        }
        ConvertTo-Json -InputObject @($apps) -Depth 3 | Set-Content -Encoding UTF8 "$out\apps.json"
        """;

    /// <summary>The SYSTEM part: an empty catalog folder the signed-in user may write in, and the script put there.</summary>
    public static string PrepareScript(string user) => $$"""
        $ErrorActionPreference = 'Stop'
        $out = '{{GuestFolder}}'
        if (Test-Path $out) { Remove-Item $out -Recurse -Force }
        New-Item -ItemType Directory $out | Out-Null
        & icacls.exe $out /grant '{{user.Replace("'", "''", StringComparison.Ordinal)}}:(OI)(CI)M' | Out-Null
        'result=ok'
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Reads the apps.json the script wrote (PowerShell's UTF-8 with a byte-order mark).</summary>
    public static IReadOnlyList<WindowsApp> Parse(string json)
    {
        var list = new List<WindowsApp>();
        if (JsonNode.Parse(json.TrimStart('\uFEFF')) is not JsonArray items) return list;
        foreach (var item in items.OfType<JsonObject>())
            if (item["id"].Str() is { Length: > 0 } id && item["name"].Str() is { Length: > 0 } name)
                list.Add(new WindowsApp(id, name, item["program"].Str() is { Length: > 0 } p ? p : null, item["icon"].Str() is { } i && IconName().IsMatch(i) ? i : null,
                    item["arguments"].Str() is { Length: > 0 } a ? a : null));
        return list;
    }

    /// <summary>Where a machine's catalog is kept on this computer.</summary>
    public static string HostFolder(string machineDirectory) => Path.Combine(machineDirectory, "windows-apps");

    public static string ListFile(string machineDirectory) => Path.Combine(HostFolder(machineDirectory), "apps.json");

    /// <summary>The catalog kept from the last time Windows was asked.</summary>
    public static IReadOnlyList<WindowsApp> Read(string machineDirectory)
    {
        try { return File.Exists(ListFile(machineDirectory)) ? Parse(File.ReadAllText(ListFile(machineDirectory))) : []; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>
    /// Asks Windows for its Start menu now and keeps the list and icons in the machine folder. Needs someone signed in
    /// (on Windows' own screen or through Windows apps). Returns the list, or why not.
    /// </summary>
    public static async Task<(IReadOnlyList<WindowsApp>? Apps, string? Why)> RefreshAsync(GuestAgentClient agent, string machineDirectory, string user, CancellationToken ct)
    {
        if (await WindowsAppsSession.EnsureLauncherAsync(agent, ct).ConfigureAwait(false) is { } noLauncher) return (null, noLauncher);
        var prepared = await WindowsAppsSession.PowerShellAsync(agent, PrepareScript(user), TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (prepared is null || !prepared.Output.Contains("result=ok", StringComparison.Ordinal))
            return (null, $"Windows could not make the app list's folder ({(prepared is null ? agent.LastError : (prepared.Errors + prepared.Output).Trim())}).");
        var script = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(UserScript().Replace("\n", "\r\n", StringComparison.Ordinal))).ToArray();
        if (await agent.WriteFileAsync(GuestFolder + @"\list-apps.ps1", new MemoryStream(script), null, ct).ConfigureAwait(false) is null)
            return (null, $"The app list script could not be copied into Windows ({agent.LastError ?? "no answer"}).");
        var run = await agent.ExecAsync(WindowsAppsLauncher.Exe, ["script", GuestFolder + @"\list-apps.ps1"], null, TimeSpan.FromMinutes(3), ct).ConfigureAwait(false);
        if (run is null) return (null, $"Windows' guest agent did not answer ({agent.LastError ?? "no answer"}).");
        if (run.Output.Contains("result=no-session", StringComparison.Ordinal))
            return (null, "Nobody is signed in to Windows. Sign in on its screen once (or open any Windows app), then refresh the list.");
        using var json = new MemoryStream();
        if (await agent.ReadFileAsync(GuestFolder + @"\apps.json", json, null, ct).ConfigureAwait(false) is null)
            return (null, $"Windows did not write its app list ({(run.Output + run.Errors).Trim()}).");
        var apps = Parse(Encoding.UTF8.GetString(json.ToArray()));

        var folder = HostFolder(machineDirectory);
        var fresh = folder + ".new";
        if (Directory.Exists(fresh)) Directory.Delete(fresh, true);
        Directory.CreateDirectory(fresh);
        foreach (var app in apps.Where(a => a.Icon is not null))
        {
            await using var icon = File.Create(Path.Combine(fresh, app.Icon!));
            await agent.ReadFileAsync(GuestFolder + @"\" + app.Icon, icon, null, ct).ConfigureAwait(false);
        }

        await File.WriteAllBytesAsync(Path.Combine(fresh, "apps.json"), json.ToArray(), ct).ConfigureAwait(false);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.Move(fresh, folder);
        return (apps, null);
    }
}
