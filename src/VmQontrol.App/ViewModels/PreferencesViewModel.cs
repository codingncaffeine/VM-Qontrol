using System.Collections.ObjectModel;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmQontrol.Core;
using VmQontrol.Core.Downloads;
using VmQontrol.Core.Hosts;

namespace VmQontrol.App.ViewModels;

/// <summary>A library folder in the preferences: where it is, how many machines it holds, whether it is the default.</summary>
public sealed record LibraryRootRow(string Path, bool IsDefault, int Machines)
{
    public string Detail => (IsDefault ? "New machines go here. " : "") + $"{Machines} machine{(Machines == 1 ? "" : "s")}";

    public bool CanRemove => !IsDefault;
}

/// <summary>
/// The app's own preferences (phase 12): every change applies at once and is saved at once; each row says what it does.
/// File work (autostart entry, the guest tools disc) runs on workers.
/// </summary>
public sealed partial class PreferencesViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private bool _loading = true;

    public PreferencesViewModel(ShellViewModel shell)
    {
        _shell = shell;
        var s = shell.Settings;
        Theme = ThemeOptions.FirstOrDefault(o => o.Value == s.Theme) ?? ThemeOptions[0];
        Viewer = ViewerOptions.FirstOrDefault(o => o.Value == s.Viewer) ?? ViewerOptions[0];
        ConfirmPowerOff = s.ConfirmPowerOff;
        ConfirmReset = s.ConfirmReset;
        StartMinimized = s.StartMinimized;
        TrayIcon = s.TrayIcon;
        Size = SizeOptions.FirstOrDefault(o => o.Value == s.NewMachineSize) ?? SizeOptions[1];
        NewPowerOn = s.NewMachinePowerOn;
        NewLean = s.NewMachineLean;
        WarnLowMemory = s.WarnLowMemory;
        KeepFree = KeepFreeOptions.FirstOrDefault(o => o.Value == s.KeepFreeGB?.ToString(System.Globalization.CultureInfo.InvariantCulture)) ?? KeepFreeOptions[0];
        Refresh();
        _loading = false;
    }

    public event Action? Closed;

    /// <summary>Where the sign-in entry goes: the desktop's own folder, or inside an isolated profile (tests, trial runs).</summary>
    private string? AutostartHome => _shell.Paths.Isolated ? _shell.Paths.Config : null;

    public static IReadOnlyList<Option> ThemeOptions { get; } =
    [
        new("system", "Follow the desktop"),
        new("light", "Light"),
        new("dark", "Dark"),
    ];

    public static IReadOnlyList<Option> ViewerOptions { get; } =
    [
        new("auto", "Automatic"),
        new("remote-viewer", "remote-viewer"),
        new("spicy", "spicy"),
    ];

    public static IReadOnlyList<Option> SizeOptions { get; } =
    [
        new("lighter", "Lighter"),
        new("recommended", "Recommended"),
        new("generous", "Generous"),
    ];

    public static IReadOnlyList<Option> KeepFreeOptions { get; } =
    [
        new(null, "Automatic"),
        new("2", "2 GB"),
        new("4", "4 GB"),
        new("8", "8 GB"),
        new("16", "16 GB"),
        new("32", "32 GB"),
    ];

    [ObservableProperty] private Option _size;
    [ObservableProperty] private bool _newPowerOn;
    [ObservableProperty] private bool _newLean;
    [ObservableProperty] private bool _warnLowMemory;
    [ObservableProperty] private Option _keepFree;
    [ObservableProperty] private string _memoryText = "Looking…";

    /// <summary>What the chosen size means, in words.</summary>
    public string SizeHelp => Size.Value switch
    {
        "lighter" => "Half the recommended memory and processors (never below what the system needs), at most a quarter of this computer's memory. For a computer you also work on while machines run.",
        "generous" => "Twice the recommendation, up to three quarters of this computer's memory and processors. For a computer that mostly runs machines.",
        _ => "Sized for the system and this computer, leaving at least half of its memory and processors for it. The wizard shows the numbers and why; you can change them there.",
    };

    [ObservableProperty] private Option _theme;
    [ObservableProperty] private Option _viewer;
    [ObservableProperty] private bool _confirmPowerOff;
    [ObservableProperty] private bool _confirmReset;
    [ObservableProperty] private bool _startAtSignIn;
    [ObservableProperty] private bool _startMinimized;
    [ObservableProperty] private bool _trayIcon;
    [ObservableProperty] private string _guestToolsText = "Looking…";
    [ObservableProperty] private bool _guestToolsHere;

    public ObservableCollection<LibraryRootRow> Roots { get; } = [];

    /// <summary>Rows and file facts, read again (the folders' machines, the autostart entry, the guest tools disc).</summary>
    public void Refresh()
    {
        Roots.Clear();
        var roots = _shell.Settings.LibraryRoots;
        for (var i = 0; i < roots.Count; i++)
        {
            var root = Path.GetFullPath(roots[i]).TrimEnd('/');
            Roots.Add(new LibraryRootRow(roots[i], i == 0, _shell.Machines.Count(m => string.Equals(Path.GetDirectoryName(m.Folder), root, StringComparison.Ordinal))));
        }

        var paths = _shell.Paths;
        var home = AutostartHome;
        var settings = _shell.Settings;
        _ = Task.Run(() =>
        {
            var (total, available) = MemoryHeadroom.ReadHost();
            var memory = total <= 0
                ? "This computer's memory could not be read, so no warning can be given."
                : $"This computer has {MemoryHeadroom.Gb(total)}; {MemoryHeadroom.Gb(available)} is free now. "
                  + (settings.KeepFreeGB is null ? $"Automatic keeps {MemoryHeadroom.Gb(MemoryHeadroom.AutomaticKeepFreeMiB(total))} free (an eighth of it, between 4 and 16 GB)." : $"{MemoryHeadroom.Gb(settings.KeepFreeMiB(total))} stays free for it.");
            var autostart = Autostart.IsOn(home);
            var disc = GuestToolsDownload.Existing(paths);
            var size = disc is null ? 0 : new FileInfo(disc).Length;
            Ui.Post(() =>
            {
                MemoryText = memory;
                var loading = _loading;
                _loading = true;
                StartAtSignIn = autostart;
                _loading = loading;
                GuestToolsHere = disc is not null;
                GuestToolsText = disc is null
                    ? "Not downloaded. Machine pages download it when you ask for the guest tools."
                    : $"Downloaded: {Formats.Bytes(size)} in {Path.GetDirectoryName(disc)}. Every Windows machine uses this one copy.";
            });
        });
    }

    private void Save(Func<AppSettings, AppSettings> change)
    {
        if (_loading) return;
        _shell.UpdateSettings(change);
    }

    partial void OnThemeChanged(Option value)
    {
        // Opening the page shows the saved theme; only a change by the person applies one.
        if (_loading) return;
        Save(s => s with { Theme = value.Value ?? "system" });
        ApplyTheme(value.Value);
    }

    /// <summary>Sets the app's light or dark look now (the desktop's choice for "system").</summary>
    public static void ApplyTheme(string? theme)
    {
        if (Avalonia.Application.Current is not { } app) return;
        app.RequestedThemeVariant = theme switch { "light" => ThemeVariant.Light, "dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };
    }

    partial void OnViewerChanged(Option value) => Save(s => s with { Viewer = value.Value ?? "auto" });

    partial void OnConfirmPowerOffChanged(bool value) => Save(s => s with { ConfirmPowerOff = value });

    partial void OnConfirmResetChanged(bool value) => Save(s => s with { ConfirmReset = value });

    partial void OnStartMinimizedChanged(bool value) => Save(s => s with { StartMinimized = value });

    partial void OnTrayIconChanged(bool value) => Save(s => s with { TrayIcon = value });

    partial void OnSizeChanged(Option value)
    {
        OnPropertyChanged(nameof(SizeHelp));
        Save(s => s with { NewMachineSize = value.Value ?? "recommended" });
    }

    partial void OnNewPowerOnChanged(bool value) => Save(s => s with { NewMachinePowerOn = value });

    partial void OnNewLeanChanged(bool value) => Save(s => s with { NewMachineLean = value });

    partial void OnWarnLowMemoryChanged(bool value) => Save(s => s with { WarnLowMemory = value });

    partial void OnKeepFreeChanged(Option value)
    {
        if (_loading) return;
        Save(s => s with { KeepFreeGB = value.Value is { } v ? int.Parse(v, System.Globalization.CultureInfo.InvariantCulture) : null });
        Refresh();
    }

    partial void OnStartAtSignInChanged(bool value)
    {
        if (_loading) return;
        var program = Environment.ProcessPath ?? "vmqontrol";
        var home = AutostartHome;
        _ = Task.Run(() =>
        {
            try
            {
                Autostart.Set(value, program, home);
                Log.Info(value ? $"Opens at sign-in ({Autostart.Path(home)})." : "No longer opens at sign-in.");
                Ui.Post(() => _shell.Note(value ? "VM Qontrol opens when you sign in." : "VM Qontrol no longer opens when you sign in."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Ui.Post(() => _shell.Note($"The sign-in entry could not be changed: {ex.Message}"));
            }
        });
    }

    [RelayCommand]
    private void RemoveRoot(LibraryRootRow row)
    {
        if (row.IsDefault) return;
        _shell.UpdateSettings(s => s with { LibraryRoots = [.. s.LibraryRoots.Where(r => r != row.Path)] });
        _shell.Note($"“{Path.GetFileName(row.Path)}” is no longer in the library. Its machines are still on the disk; add the folder again to list them.");
        _ = _shell.RefreshAsync().ContinueWith(_ => Ui.Post(Refresh), TaskScheduler.Default);
    }

    [RelayCommand]
    private void MakeDefault(LibraryRootRow row)
    {
        if (row.IsDefault) return;
        _shell.UpdateSettings(s => s with { LibraryRoots = [row.Path, .. s.LibraryRoots.Where(r => r != row.Path)] });
        Refresh();
    }

    public async Task AddRootAsync(string folder)
    {
        await _shell.AddLibraryFolderAsync(folder).ConfigureAwait(false);
        Ui.Post(Refresh);
    }

    [RelayCommand]
    private void RemoveGuestTools()
    {
        var paths = _shell.Paths;
        _ = Task.Run(() =>
        {
            try
            {
                File.Delete(GuestToolsDownload.Target(paths));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Ui.Post(() => _shell.Note($"The guest tools disc could not be removed: {ex.Message}"));
            }

            Ui.Post(() =>
            {
                _ = _shell.FindGuestToolsAsync();
                Refresh();
            });
        });
    }

    // ── About ──

    private bool _aboutRequested;

    /// <summary>Raised when the page should scroll to About (the view may not exist yet: it takes the request on load).</summary>
    public event Action? AboutRequested;

    public void RequestAbout()
    {
        _aboutRequested = true;
        AboutRequested?.Invoke();
    }

    /// <summary>The view takes a pending scroll-to-About once.</summary>
    public bool TakeAboutRequest()
    {
        var was = _aboutRequested;
        _aboutRequested = false;
        return was;
    }

    /// <summary>"0.0.5".</summary>
    public string AppVersion => UpdateCheck.ThisVersion;

    /// <summary>"Version 0.0.5 · build c776d83": the build line a bug report needs.</summary>
    public string VersionLine => BuildInfo.Version.Split('+') is [var v, var rev, ..] ? $"Version {v} · build {rev[..Math.Min(7, rev.Length)]}" : $"Version {BuildInfo.Version}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatestText), nameof(UpdateAvailable), nameof(CheckButtonText))]
    private bool _checkingUpdates;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatestText))]
    private string? _checkProblem;

    /// <summary>A newer release than this build is known (from the last Check for updates).</summary>
    public bool UpdateAvailable => !CheckingUpdates && UpdateCheck.IsNewer(_shell.Settings.LatestKnownVersion, AppVersion);

    public string CheckButtonText => CheckingUpdates ? "Checking…" : "Check for updates";

    /// <summary>This version against the newest release known: nothing is fetched until the person asks.</summary>
    public string LatestText
    {
        get
        {
            if (CheckingUpdates) return "Asking GitHub for the newest release…";
            var s = _shell.Settings;
            var when = s.LatestCheckedAt is { } at ? $" (checked {Ago(at)})" : "";
            var known = UpdateCheck.Normalize(s.LatestKnownVersion);
            var line = known is null
                ? "Newest release: not checked yet. Check for updates asks GitHub once; nothing about you or this computer is sent."
                : UpdateCheck.IsNewer(known, AppVersion)
                    ? $"Version {known} is available{when}. {HowToUpdate}"
                    : $"This is the newest release{when}.";
            return CheckProblem is { } p ? $"{p} {line}" : line;
        }
    }

    /// <summary>The way this copy was installed decides how it is updated.</summary>
    private static string HowToUpdate =>
        Environment.ProcessPath?.StartsWith("/usr/", StringComparison.Ordinal) != true
            ? "Download the new tarball from the release page and replace this folder."
            : File.Exists("/usr/bin/pacman")
                ? "Update it with your AUR helper (package vm-qontrol-bin)."
                : "Install the new .deb from the release page.";

    private static string Ago(DateTimeOffset at)
    {
        var age = DateTimeOffset.Now - at;
        return age.TotalMinutes < 2 ? "just now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} minutes ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} hour{((int)age.TotalHours == 1 ? "" : "s")} ago"
            : at.LocalDateTime.ToString("MMMM d, yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        if (CheckingUpdates) return;
        CheckingUpdates = true;
        CheckProblem = null;
        try
        {
            var latest = await Task.Run(() => UpdateCheck.LatestAsync()).ConfigureAwait(false);
            Ui.Post(() =>
            {
                _shell.UpdateSettings(s => s with { LatestKnownVersion = latest.Version, LatestKnownUrl = latest.Url, LatestCheckedAt = DateTimeOffset.Now });
                CheckingUpdates = false;
                _shell.Note(UpdateCheck.IsNewer(latest.Version, AppVersion) ? $"VM Qontrol {latest.Version} is available." : "VM Qontrol is up to date.");
            });
        }
        catch (IOException ex)
        {
            Ui.Post(() =>
            {
                CheckProblem = ex.Message;
                CheckingUpdates = false;
            });
        }
    }

    [RelayCommand]
    private void OpenRelease() => Platform.Links.Open(_shell.Settings.LatestKnownUrl ?? UpdateCheck.ReleasesPage);

    [RelayCommand]
    private void OpenLink(string url) => Platform.Links.Open(url);

    [RelayCommand]
    private void Close() => Closed?.Invoke();
}
