using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmQontrol.Core.Runtime;
using VmQontrol.Core.WindowsApps;

namespace VmQontrol.App.ViewModels;

/// <summary>One Windows program on the machine page: its icon, its name, whether it is in this computer's app menu.</summary>
public sealed partial class WindowsAppItem(WindowsApp app, Bitmap? icon, bool inMenu) : ObservableObject
{
    public WindowsApp App { get; } = app;

    public string Name => App.Name;

    public Bitmap? Icon { get; } = icon;

    public bool HasIcon => Icon is not null;

    public string Kind => App.Packaged ? "Store app" : App.Program ?? "";

    [ObservableProperty]
    private bool _inMenu = inMenu;

    [ObservableProperty]
    private bool _opening;
}

/// <summary>
/// D43, the machine page's "Windows apps" tab: turn the feature on (Windows sets Remote Desktop up through the guest
/// agent and lists its Start menu), open any program as its own window here, and put programs in this computer's app
/// menu. Every step that touches the guest, the disk or the keyring runs on a worker; results are posted back.
/// </summary>
public sealed partial class WindowsAppsViewModel(MachineViewModel machine, ShellViewModel shell) : ObservableObject
{
    private List<WindowsAppItem> _all = [];

    public ObservableCollection<WindowsAppItem> Apps { get; } = [];

    /// <summary>The headline: what state Windows apps are in, in words.</summary>
    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _detail = "";

    /// <summary>A step is running (setting up, listing, opening).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTurnOn), nameof(CanRefresh))]
    private bool _busy;

    [ObservableProperty]
    private string _progress = "";

    /// <summary>The list has been read at least once.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTurnOn), nameof(ShowIntro), nameof(CanRefresh))]
    private bool _hasList;

    [ObservableProperty]
    private string _problem = "";

    public bool HasProblem => Problem.Length > 0;

    partial void OnProblemChanged(string value) => OnPropertyChanged(nameof(HasProblem));

    [ObservableProperty]
    private string _freeRdpMissing = "";

    public bool HasFreeRdpMissing => FreeRdpMissing.Length > 0;

    partial void OnFreeRdpMissingChanged(string value) => OnPropertyChanged(nameof(HasFreeRdpMissing));

    /// <summary>A button installs FreeRDP (the distribution's package manager through polkit); else the command is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFreeRdpCommand))]
    private bool _canInstallFreeRdp;

    public bool ShowFreeRdpCommand => !CanInstallFreeRdp;

    [ObservableProperty]
    private bool _installingFreeRdp;

    /// <summary>Installs FreeRDP with one password prompt, then checks again.</summary>
    [RelayCommand]
    private async Task InstallFreeRdpAsync()
    {
        if (InstallingFreeRdp) return;
        InstallingFreeRdp = true;
        Problem = "";
        var why = await Task.Run(async () =>
        {
            var failed = await Core.Hosts.PackageInstall.InstallAsync("freerdp").ConfigureAwait(false);
            return failed ?? (WindowsAppsClient.FindFreeRdp() is null ? "The package installed, but FreeRDP 3's client was not found afterwards." : null);
        }).ConfigureAwait(false);
        Ui.Post(() =>
        {
            InstallingFreeRdp = false;
            if (why is null)
            {
                FreeRdpMissing = "";
                shell.Note("FreeRDP is installed: Windows programs can open as windows here.");
            }
            else Problem = why;
        });
    }

    [ObservableProperty]
    private string _search = "";

    partial void OnSearchChanged(string value) => Filter();

    public bool ShowIntro => !HasList;

    public bool CanTurnOn => !Busy && !HasList;

    public bool CanRefresh => !Busy && HasList;

    [ObservableProperty]
    private string _countText = "";

    private bool _loaded;

    /// <summary>Reads what is kept on this computer (the list and icons from last time); no guest work.</summary>
    public async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        var config = machine.Machine;
        var folder = machine.Folder;
        var desktop = shell.Paths.DesktopData;
        var (items, missing, installable) = await Task.Run(() =>
        {
            var apps = WindowsAppsCatalog.Read(folder);
            var list = apps.Select(a => new WindowsAppItem(a, LoadIcon(folder, a), WindowsAppsMenu.Has(config, a, desktop))).ToList();
            // The window program may have moved (a new build, an unpacked tarball): entries follow it.
            foreach (var item in list.Where(i => i.InMenu)) WindowsAppsMenu.Add(config, folder, item.App, desktop);
            var missingRdp = WindowsAppsClient.FindFreeRdp() is null;
            return (list, missingRdp ? WindowsAppsClient.FreeRdpInstall : "", missingRdp && Core.Hosts.PackageInstall.CanInstall("freerdp"));
        }).ConfigureAwait(false);
        Ui.Post(() =>
        {
            FreeRdpMissing = missing;
            CanInstallFreeRdp = installable;
            Show(items);
        });
    }

    private static Bitmap? LoadIcon(string folder, WindowsApp app)
    {
        if (app.Icon is null) return null;
        var file = Path.Combine(WindowsAppsCatalog.HostFolder(folder), app.Icon);
        try
        {
            using var stream = File.OpenRead(file);
            return Bitmap.DecodeToWidth(stream, 64);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private void Show(List<WindowsAppItem> items)
    {
        _all = items;
        HasList = items.Count > 0;
        Status = HasList ? $"{items.Count} Windows programs can open as windows here" : "Open Windows programs as windows on this desktop";
        Detail = HasList
            ? "Open one, or put it in this computer's app menu. While programs are open, Windows' own screen shows its sign-in page; signing in there takes the programs back into it."
            : "Each program gets its own window on this desktop, with no Windows desktop around it, and can have its own entry in this computer's app menu. Needs Windows Pro, Enterprise or Education, and the guest tools.";
        Filter();
    }

    private void Filter()
    {
        var words = Search.Trim();
        Apps.Clear();
        foreach (var item in _all.Where(i => words.Length == 0 || i.Name.Contains(words, StringComparison.OrdinalIgnoreCase)))
            Apps.Add(item);
        CountText = words.Length == 0 ? "" : $"{Apps.Count} of {_all.Count}";
    }

    /// <summary>Sets Windows up (idempotent) and reads its Start menu; the same button refreshes the list later.</summary>
    [RelayCommand]
    private async Task TurnOnAsync()
    {
        if (Busy) return;
        Busy = true;
        Problem = "";
        Progress = "Setting Windows up…";
        var config = machine.Machine;
        var folder = machine.Folder;
        var desktop = shell.Paths.DesktopData;
        var paths = shell.Paths;
        var host = await shell.HostAsync().ConfigureAwait(false);
        var progress = new Progress<string>(text => Ui.Post(() => Progress = text));
        var (items, why) = await Task.Run(async () =>
        {
            var (running, notReady) = await WindowsAppsPower.ReadyAsync(config, folder, paths, () => host, progress, CancellationToken.None).ConfigureAwait(false);
            if (running is null) return (null, notReady);
            ((IProgress<string>)progress).Report("Turning Remote Desktop on in Windows…");
            var agent = new GuestAgentClient(running.Paths.Agent);
            var ready = await WindowsAppsSetup.PrepareAsync(agent, CancellationToken.None).ConfigureAwait(false);
            if (!ready.Ready) return (null, ready.Why);
            ((IProgress<string>)progress).Report("Reading Windows' Start menu…");
            var (apps, noList) = await WindowsAppsCatalog.RefreshAsync(agent, folder, ready.SignedIn ?? config.EasyInstall?.UserName ?? "Users", CancellationToken.None).ConfigureAwait(false);
            if (apps is null) return (null, noList);
            return ((List<WindowsAppItem>?)apps.Select(a => new WindowsAppItem(a, LoadIcon(folder, a), WindowsAppsMenu.Has(config, a, desktop))).ToList(), (string?)null);
        }).ConfigureAwait(false);
        Ui.Post(() =>
        {
            Busy = false;
            Progress = "";
            if (items is null) Problem = why ?? "Windows apps could not be set up.";
            else Show(items);
        });
    }

    [RelayCommand]
    private Task RefreshAsync() => TurnOnAsync();

    /// <summary>
    /// Turns the feature off: Remote Desktop off inside Windows again (when the machine runs; otherwise at the next Turn
    /// on it is set up again anyway), this machine's entries out of the app menu, its kept password forgotten, the list
    /// cleared.
    /// </summary>
    [RelayCommand]
    private async Task TurnOffAsync()
    {
        if (Busy) return;
        Busy = true;
        Problem = "";
        Progress = "Turning Windows apps off…";
        var config = machine.Machine;
        var folder = machine.Folder;
        var paths = shell.Paths;
        var note = await Task.Run(async () =>
        {
            string? why = null;
            if (MachineRunner.Find(config, paths) is { } running)
            {
                var undone = await WindowsAppsSetup.PrepareAsync(new GuestAgentClient(running.Paths.Agent), CancellationToken.None, undo: true).ConfigureAwait(false);
                if (!undone.Ready) why = $"Windows did not turn Remote Desktop off ({undone.Why})";
            }

            WindowsAppsMenu.RemoveAll(config.Id, paths.DesktopData);
            foreach (var account in new[] { config.WindowsApps?.Account, WindowsAppsClient.ClientAccount(new Core.Qemu.RuntimePaths(paths.RuntimeFor(config.Id))), config.EasyInstall?.UserName }.OfType<string>().Distinct())
                await WindowsAppsPasswords.ForgetAsync(config, account, CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (Directory.Exists(WindowsAppsCatalog.HostFolder(folder))) Directory.Delete(WindowsAppsCatalog.HostFolder(folder), true);
            }
            catch (IOException ex)
            {
                why ??= $"The kept list could not be removed ({ex.Message})";
            }

            return why;
        }).ConfigureAwait(false);
        Ui.Post(() =>
        {
            Busy = false;
            Progress = "";
            Show([]);
            if (note is not null) Problem = note + ".";
            else shell.Note($"Windows apps are off for {config.Name}.");
        });
    }

    /// <summary>Opens the program as its own window here (powering the machine on first when it is off).</summary>
    [RelayCommand]
    private async Task OpenAsync(WindowsAppItem? item)
    {
        if (item is null || item.Opening) return;
        item.Opening = true;
        Problem = "";
        var config = machine.Machine;
        var folder = machine.Folder;
        var paths = shell.Paths;
        var host = await shell.HostAsync().ConfigureAwait(false);
        var result = await Task.Run(async () =>
        {
            var (running, notReady) = await WindowsAppsPower.ReadyAsync(config, folder, paths, () => host, new Progress<string>(), CancellationToken.None).ConfigureAwait(false);
            if (running is null) return new WindowsAppsClient.OpenResult(false, notReady);
            return await WindowsAppsPasswords.OpenAsync(config, folder, paths, running, item.App, AskPasswordAsync, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);
        Ui.Post(() =>
        {
            item.Opening = false;
            if (!result.Opened) Problem = result.Why ?? "The program did not open.";
            else if (result.Kept is { Length: > 0 } kept) shell.Note($"The Windows password was not kept: {kept}.");
        });
    }

    /// <summary>Asks for the Windows account's password in the window's own prompt.</summary>
    private async Task<string?> AskPasswordAsync(string account, bool refused, CancellationToken ct)
    {
        var prompt = await Ui.InvokeAsync(() =>
        {
            var p = new PromptViewModel(
                refused ? $"Windows refused the password for {account}" : $"Sign in to Windows on {machine.Name}",
                [refused
                    ? "Type the password again; it is the one this account uses to sign in to Windows."
                    : $"Windows programs open as {account}. The password is kept in this desktop's keyring, never in VM Qontrol's files."],
                "Password", "", "Sign in", text => text.Length > 0) { IsSecret = true };
            shell.ShowPrompt(p);
            return p;
        }).ConfigureAwait(false);
        var answer = await prompt.Answer.ConfigureAwait(false);
        Ui.Post(prompt.Finish);
        return answer;
    }

    /// <summary>Adds the program to this computer's app menu, or takes it out.</summary>
    [RelayCommand]
    private async Task ToggleMenuAsync(WindowsAppItem? item)
    {
        if (item is null) return;
        var config = machine.Machine;
        var folder = machine.Folder;
        var desktop = shell.Paths.DesktopData;
        var want = item.InMenu;
        var error = await Task.Run(() =>
        {
            try
            {
                if (want) WindowsAppsMenu.Add(config, folder, item.App, desktop);
                else WindowsAppsMenu.Remove(config, item.App, desktop);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ex.Message;
            }
        }).ConfigureAwait(false);
        Ui.Post(() =>
        {
            if (error is null) shell.Note(want ? $"{item.Name} is in this computer's app menu." : $"{item.Name} left this computer's app menu.");
            else
            {
                item.InMenu = !want;
                Problem = $"The app menu could not be changed ({error}).";
            }
        });
    }
}
