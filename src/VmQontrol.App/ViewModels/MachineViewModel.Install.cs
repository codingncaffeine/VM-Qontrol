using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmQontrol.Core;
using VmQontrol.Core.Install;
using VmQontrol.Core.Machines;
using VmQontrol.Core.Runtime;

namespace VmQontrol.App.ViewModels;

/// <summary>The "Installing for you" card: what Easy Install is doing, from power on to the guest's own "finished".</summary>
public sealed partial class MachineViewModel
{
    [ObservableProperty]
    private InstallProgress _install = new(InstallStage.None);

    private bool IsWindowsGuest => Machine.Guest.Os is "windows" or "windows-server";

    private string InstallSystem => Machine.EasyInstall?.System is { Length: > 0 } s ? s : GuestLabel;

    public bool ShowInstallCard => Install.Stage is InstallStage.NotStarted or InstallStage.Installing
                                   || (Install.Stage == InstallStage.Finished && !Install.Acknowledged)
                                   || ShowEasyInstallOffer;

    /// <summary>The disk is empty, an installer disc is in, and nothing answers it: offer Easy Install (MachineFactory.CanAddEasyInstall).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEasyInstallOffer))]
    private bool _canAddEasyInstall;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEasyInstallOffer))]
    private bool _easyInstallOfferDismissed;

    public bool ShowEasyInstallOffer => CanAddEasyInstall && !EasyInstallOfferDismissed && Install.Stage == InstallStage.None && IsOff;

    partial void OnCanAddEasyInstallChanged(bool value) => NotifyInstall();

    partial void OnEasyInstallOfferDismissedChanged(bool value) => NotifyInstall();

    [RelayCommand]
    private void SetUpEasyInstall() => _shell.SetUpEasyInstall(this);

    [RelayCommand]
    private void DismissEasyInstallOffer() => EasyInstallOfferDismissed = true;

    public bool InstallWorking => Install.Stage == InstallStage.Installing && IsActive;

    public bool InstallWaiting => Install.Stage == InstallStage.Installing && !IsActive;

    public bool InstallFinished => Install.Stage == InstallStage.Finished;

    public bool InstallNotStarted => Install.Stage == InstallStage.NotStarted;

    public string InstallTitle => ShowEasyInstallOffer ? $"Let VM Qontrol install {GuestLabel} for you?" : Install.Stage switch
    {
        InstallStage.NotStarted => $"{InstallSystem} installs itself",
        InstallStage.Installing when IsActive => $"Installing {InstallSystem} for you",
        InstallStage.Installing => $"Installing {InstallSystem}: waiting for power",
        InstallStage.Finished => $"{InstallSystem} is installed",
        _ => "",
    };

    private string Account => Machine.EasyInstall?.UserName is { Length: > 0 } u ? u : "your account";

    private string EndsAt => IsWindowsGuest
        ? $"at the Windows desktop, signed in as {Account}"
        : $"at the sign-in screen, ready for {Machine.Guest.Account ?? Account} and the password you chose";

    public string InstallBody => ShowEasyInstallOffer
        ? "This machine's disk is still empty and its installer disc is in. Easy Install answers every installer question from a name and password you enter once; you come back to a finished system instead of clicking through Setup."
        : Install.Stage switch
    {
        InstallStage.NotStarted =>
            $"Power it on and VM Qontrol answers every question Setup asks. The machine restarts by itself a few times and ends {EndsAt}. There is nothing to press.",
        InstallStage.Installing when IsActive =>
            $"Nothing to press: VM Qontrol answered Setup's questions. The machine restarts by itself a few times and ends {EndsAt}. "
            + "Closing the viewer or this app does not stop it; this card says when it is done.",
        InstallStage.Installing =>
            "The machine was turned off part way through. Power it on and Setup carries on from where it was.",
        InstallStage.Finished => (Install.Took is { } took ? $"Installed for you in {Minutes(took)}. " : "Installed for you. ")
                                 + (IsWindowsGuest ? $"It signs in as {Account} by itself." : $"Sign in as {Machine.Guest.Account ?? Account} with the password you chose."),
        _ => "",
    };

    public string InstallTime => Install.Stage switch
    {
        InstallStage.Installing when Install.Started is { } s =>
            $"Started {Ago(DateTimeOffset.Now - s)} · usually takes {(IsWindowsGuest ? "20 to 30" : "10 to 25")} minutes",
        InstallStage.NotStarted or InstallStage.Installing => $"Usually takes {(IsWindowsGuest ? "20 to 30" : "10 to 25")} minutes",
        _ => "",
    };

    public bool HasInstallTime => InstallTime.Length > 0;

    public bool ShowLeanNote => Machine.EasyInstall?.Lean == true && Install.Stage != InstallStage.Finished;

    public string LeanNote => "Lean Windows is on: promotions, games and ads are left out while it installs.";

    private static string Minutes(TimeSpan t) => t.TotalMinutes < 1.5 ? "a minute" : $"{Math.Round(t.TotalMinutes)} minutes";

    private static string Ago(TimeSpan t) => t.TotalMinutes < 1 ? "just now" : t.TotalMinutes < 1.5 ? "a minute ago" : $"{Math.Round(t.TotalMinutes)} minutes ago";

    partial void OnInstallChanged(InstallProgress? oldValue, InstallProgress newValue)
    {
        if (oldValue?.Stage == InstallStage.Installing && newValue.Stage == InstallStage.Finished)
            _shell.Note($"{InstallSystem} finished installing on {Name}.");
        NotifyInstall();
    }

    // The card's words depend on whether the machine is on.
    partial void OnStateChanged(PowerState value) => NotifyInstall();

    private void NotifyInstall()
    {
        foreach (var name in new[]
                 {
                     nameof(ShowInstallCard), nameof(InstallWorking), nameof(InstallWaiting), nameof(InstallFinished), nameof(InstallNotStarted),
                     nameof(ShowEasyInstallOffer), nameof(InstallTitle), nameof(InstallBody), nameof(InstallTime), nameof(HasInstallTime), nameof(ShowLeanNote), nameof(LibraryLabel),
                 })
            OnPropertyChanged(name);
    }

    /// <summary>Reads the signal file on a worker; texts that depend on the power state are re-announced either way.</summary>
    private async Task RefreshInstallAsync()
    {
        var machine = Machine;
        var dir = Entry.Directory;
        InstallProgress progress;
        bool offer;
        try
        {
            (progress, offer) = await Task.Run(() => (InstallSignal.Read(machine, dir), MachineFactory.CanAddEasyInstall(machine, dir))).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"{Name}: could not read the install signal ({ex.Message}).");
            return;
        }

        Ui.Post(() =>
        {
            CanAddEasyInstall = offer;
            if (Install == progress) NotifyInstall();
            else Install = progress;
        });
    }

    /// <summary>While the machine runs and installs: a look every five seconds (the elapsed time moves too).</summary>
    private async Task InstallLoopAsync(MachineWatcher watcher)
    {
        if (Machine.EasyInstall is null) return;
        while (!watcher.Exited.IsCompleted)
        {
            await RefreshInstallAsync().ConfigureAwait(false);
            if (Install.Stage is InstallStage.Finished or InstallStage.None) return;
            await Task.WhenAny(watcher.Exited, Task.Delay(5000)).ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private async Task AcknowledgeInstallAsync()
    {
        var dir = Entry.Directory;
        var finished = Install.Finished ?? DateTimeOffset.Now;
        try
        {
            await Task.Run(() => InstallSignal.Acknowledge(dir, finished)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"{Name}: could not note the install card as read ({ex.Message}).");
        }

        await RefreshInstallAsync().ConfigureAwait(false);
    }
}
