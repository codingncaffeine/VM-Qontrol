namespace VmQontrol.Core.Install;

/// <summary>
/// "Lean Windows": the bundled promotions, games and ads taken out of an official Windows while it installs (D29), the
/// same kind of steps tiny11's open build script applies, but run on Microsoft's own disc from the answer file instead of
/// shipping a remastered image. The script runs once in the specialize pass, as SYSTEM, before any account exists, so
/// no account ever gets the removed apps; it writes what it did to C:\Windows\Setup\Scripts\vmq-lean.log.
/// Everything stays reversible: each app is in the Microsoft Store, each setting is an ordinary policy.
/// Kept on purpose: Store, Edge, Calculator, Photos, Notepad, Paint, Terminal, Snipping Tool, Camera, Clock, Sticky
/// Notes, Quick Assist, Windows Security and Windows Update.
/// </summary>
public static class WindowsLean
{
    /// <summary>The script's name on the answer disc (8.3, so every Windows reads it the same).</summary>
    public const string ScriptName = "VMQLEAN.PS1";

    /// <summary>Provisioned apps removed (Windows 10 and 11 names; one a release lacks is skipped).</summary>
    public static readonly IReadOnlyList<string> RemovedApps =
    [
        "Clipchamp.Clipchamp", "Microsoft.BingNews", "Microsoft.BingWeather", "Microsoft.BingSearch", "Microsoft.Copilot",
        "Microsoft.GamingApp", "Microsoft.GetHelp", "Microsoft.Getstarted", "Microsoft.MicrosoftOfficeHub",
        "Microsoft.MicrosoftSolitaireCollection", "Microsoft.MixedReality.Portal", "Microsoft.Office.OneNote", "Microsoft.OutlookForWindows",
        "Microsoft.People", "Microsoft.PowerAutomateDesktop", "Microsoft.SkypeApp", "Microsoft.Todos", "Microsoft.Wallet",
        "Microsoft.Windows.DevHome", "Microsoft.WindowsCommunicationsApps", "Microsoft.WindowsFeedbackHub", "Microsoft.WindowsMaps",
        "Microsoft.WindowsSoundRecorder", "Microsoft.Xbox.TCUI", "Microsoft.XboxApp", "Microsoft.XboxGameOverlay",
        "Microsoft.XboxGamingOverlay", "Microsoft.XboxIdentityProvider", "Microsoft.XboxSpeechToTextOverlay", "Microsoft.YourPhone",
        "Microsoft.ZuneMusic", "Microsoft.ZuneVideo", "Microsoft.549981C3F5F10", "MicrosoftCorporationII.MicrosoftFamily",
        "MicrosoftTeams", "MSTeams", "Microsoft.MicrosoftJournal", "Microsoft.Windows.Ai.Copilot.Provider",
    ];

    /// <summary>The specialize-pass command that finds the answer disc and runs the script from it.</summary>
    public const string Command = @"cmd /c for %d in (D E F G H I J) do if exist %d:\" + ScriptName + @" powershell -NoProfile -ExecutionPolicy Bypass -File %d:\" + ScriptName;

    public static string Script()
    {
        var apps = string.Join(",", RemovedApps.Select(a => $"'{a}'"));
        return $$"""
            # VM Qontrol "Lean Windows": runs once while Windows sets itself up (specialize pass, as SYSTEM).
            $ErrorActionPreference = 'Continue'
            $log = "$env:SystemRoot\Setup\Scripts\vmq-lean.log"
            New-Item -ItemType Directory -Force -Path (Split-Path $log) | Out-Null
            Start-Transcript -Path $log -Append | Out-Null

            # Bundled apps: removed from the image, so no account gets them.
            $remove = @({{apps}})
            Get-AppxProvisionedPackage -Online | Where-Object { $remove -contains $_.DisplayName } | ForEach-Object {
                Write-Output "Removing $($_.DisplayName)"
                Remove-AppxProvisionedPackage -Online -PackageName $_.PackageName | Out-Null
            }

            function Set-Value($path, $name, $value) {
                if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
                New-ItemProperty -Path $path -Name $name -Value $value -PropertyType DWord -Force | Out-Null
                Write-Output "Set $path\$name = $value"
            }

            # Machine policies: no suggested apps or ads, no web results or Copilot in Start, no widgets, no Chat icon,
            # diagnostic data at the lowest level the edition allows.
            $p = 'HKLM:\SOFTWARE\Policies\Microsoft\Windows'
            Set-Value "$p\CloudContent" DisableWindowsConsumerFeatures 1
            Set-Value "$p\CloudContent" DisableConsumerAccountStateContent 1
            Set-Value "$p\CloudContent" DisableCloudOptimizedContent 1
            Set-Value "$p\DataCollection" AllowTelemetry 0
            Set-Value "$p\AdvertisingInfo" DisabledByGroupPolicy 1
            Set-Value "$p\Windows Search" AllowCortana 0
            Set-Value "$p\Windows Search" DisableWebSearch 1
            Set-Value "$p\Windows Search" ConnectedSearchUseWeb 0
            Set-Value "$p\WindowsCopilot" TurnOffWindowsCopilot 1
            Set-Value "$p\Windows Chat" ChatIcon 3
            Set-Value 'HKLM:\SOFTWARE\Policies\Microsoft\Dsh' AllowNewsAndInterests 0

            # Every new account (the default profile): no OneDrive installer at sign-in, no suggestions or silently
            # installed apps, no web suggestions in the search box, no Copilot or widgets button.
            reg.exe load 'HKU\VmqDefault' "$env:SystemDrive\Users\Default\NTUSER.DAT" | Out-Null
            $u = 'Registry::HKEY_USERS\VmqDefault'
            Remove-ItemProperty -Path "$u\Software\Microsoft\Windows\CurrentVersion\Run" -Name OneDriveSetup -ErrorAction SilentlyContinue
            $cdm = "$u\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager"
            foreach ($name in 'ContentDeliveryAllowed','FeatureManagementEnabled','OemPreInstalledAppsEnabled','PreInstalledAppsEnabled',
                    'PreInstalledAppsEverEnabled','SilentInstalledAppsEnabled','SoftLandingEnabled','SystemPaneSuggestionsEnabled',
                    'SubscribedContentEnabled','SubscribedContent-310093Enabled','SubscribedContent-338388Enabled',
                    'SubscribedContent-338389Enabled','SubscribedContent-338393Enabled','SubscribedContent-353694Enabled',
                    'SubscribedContent-353696Enabled','SubscribedContent-353698Enabled') { Set-Value $cdm $name 0 }
            Set-Value "$u\Software\Policies\Microsoft\Windows\Explorer" DisableSearchBoxSuggestions 1
            Set-Value "$u\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" ShowCopilotButton 0
            Set-Value "$u\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" Start_IrisRecommendations 0
            Set-Value "$u\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" TaskbarMn 0
            Set-Value "$u\Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo" Enabled 0
            [gc]::Collect()
            reg.exe unload 'HKU\VmqDefault' | Out-Null

            Write-Output 'Lean Windows: done'
            Stop-Transcript | Out-Null
            """;
    }
}
