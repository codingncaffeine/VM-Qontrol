using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmQontrol.Core;
using VmQontrol.Core.Hosts;
using VmQontrol.Core.Library;
using VmQontrol.Core.WindowsApps;

namespace VmQontrol.App.ViewModels;

/// <summary>
/// D43: what an app menu entry opens (vmqontrol --windows-app MACHINE APP): a small window that powers the machine on
/// when it is off, asks for the Windows password only when none is kept, and closes itself once the program's window
/// is on its way. The main window is never opened for this.
/// </summary>
public sealed partial class WindowsAppLaunchViewModel(QontrolPaths paths, string machineId, string appSlug) : ObservableObject
{
    [ObservableProperty]
    private string _title = "Opening a Windows program";

    [ObservableProperty]
    private string _status = "Finding the machine…";

    [ObservableProperty]
    private Bitmap? _icon;

    [ObservableProperty]
    private bool _working = true;

    [ObservableProperty]
    private bool _failed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPassword))]
    private bool _asking;

    public bool ShowPassword => Asking;

    [ObservableProperty]
    private string _account = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private string _password = "";

    /// <summary>Raised when the window should close (opened, or the person gave up).</summary>
    public event Action? Done;

    private TaskCompletionSource<string?>? _answer;

    public async Task RunAsync()
    {
        var found = await Task.Run(() =>
        {
            var settings = AppSettings.Load(paths);
            var entry = settings.LibraryRoots.SelectMany(r => LibraryScanner.Scan(r, paths.State)).FirstOrDefault(e => e.Machine?.Id == machineId);
            if (entry?.Machine is null) return ((LibraryEntry?)null, (WindowsApp?)null, (Bitmap?)null);
            var app = WindowsAppsCatalog.Read(entry.Directory).FirstOrDefault(a => a.Slug == appSlug);
            Bitmap? icon = null;
            if (app?.Icon is { } file && File.Exists(Path.Combine(WindowsAppsCatalog.HostFolder(entry.Directory), file)))
            {
                using var stream = File.OpenRead(Path.Combine(WindowsAppsCatalog.HostFolder(entry.Directory), file));
                icon = Bitmap.DecodeToWidth(stream, 96);
            }

            return (entry, app, icon);
        }).ConfigureAwait(false);
        var (machineEntry, app, picture) = found;
        if (machineEntry?.Machine is not { } config)
        {
            Fail("The machine this program belongs to is not in VM Qontrol's library any more; remove the entry from the app menu, or put the machine back.");
            return;
        }

        if (app is null)
        {
            Fail($"{config.Name} no longer lists this program. Open VM Qontrol, then Windows apps on {config.Name}, and refresh the list.");
            return;
        }

        Ui.Post(() =>
        {
            Title = $"Opening {app.Name}";
            Icon = picture;
        });
        var progress = new Progress<string>(text => Ui.Post(() => Status = text));
        var result = await Task.Run(async () =>
        {
            var (running, why) = await WindowsAppsPower.ReadyAsync(config, machineEntry.Directory, paths, () => HostProbe.Probe(), progress, CancellationToken.None).ConfigureAwait(false);
            if (running is null) return new WindowsAppsClient.OpenResult(false, why);
            ((IProgress<string>)progress).Report($"Signing in to Windows on {config.Name}…");
            return await WindowsAppsPasswords.OpenAsync(config, machineEntry.Directory, paths, running, app, AskAsync, CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);
        if (!result.Opened)
        {
            Fail(result.Why ?? "The program did not open.");
            return;
        }

        Ui.Post(() => Status = result.Kept is { Length: > 0 } kept ? $"{app.Name} is opening. The password was not kept: {kept}." : $"{app.Name} is opening.");
        await Task.Delay(TimeSpan.FromSeconds(result.Kept is { Length: > 0 } ? 6 : 2)).ConfigureAwait(false);
        Ui.Post(() => Done?.Invoke());
    }

    private Task<string?> AskAsync(string account, bool refused, CancellationToken ct)
    {
        _answer = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Ui.Post(() =>
        {
            Account = account;
            Password = "";
            Status = refused ? $"Windows refused that password for {account}. Type it again." : $"Type the Windows password of {account}. It is kept in this desktop's keyring.";
            Working = false;
            Asking = true;
        });
        return _answer.Task;
    }

    private void Fail(string why) => Ui.Post(() =>
    {
        Status = why;
        Working = false;
        Asking = false;
        Failed = true;
    });

    private bool CanSignIn() => Password.Length > 0;

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private void SignIn()
    {
        Asking = false;
        Working = true;
        Status = "Signing in…";
        _answer?.TrySetResult(Password);
        Password = "";
    }

    [RelayCommand]
    private void Close()
    {
        _answer?.TrySetResult(null);
        Done?.Invoke();
    }
}
