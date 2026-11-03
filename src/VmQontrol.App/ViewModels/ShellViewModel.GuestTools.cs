using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmQontrol.Core.Downloads;

namespace VmQontrol.App.ViewModels;

/// <summary>
/// The Windows guest tools disc (D31), one for the whole app: found or downloaded once, then offered to every machine.
/// One download at a time, whichever page asked first; the others wait on the same task.
/// </summary>
public sealed partial class ShellViewModel
{
    private Task<string?>? _guestToolsTask;
    private CancellationTokenSource? _guestToolsCancel;
    private IProgress<DownloadProgress>? _guestToolsAlso;

    /// <summary>The disc is on this computer (downloaded, or chosen before): inserting needs no download.</summary>
    [ObservableProperty]
    private bool _hasGuestTools;

    [ObservableProperty]
    private bool _guestToolsBusy;

    [ObservableProperty]
    private bool _guestToolsSizeKnown;

    [ObservableProperty]
    private double _guestToolsPercent;

    [ObservableProperty]
    private string _guestToolsStatus = "";

    public string GuestToolsSize => GuestToolsDownload.ApproximateSize;

    /// <summary>The disc to use without asking, checked on a worker: the one chosen before if it is the driver disc, else the downloaded one.</summary>
    public Task<string?> FindGuestToolsAsync()
    {
        var remembered = Settings.DriversIso;
        var paths = Paths;
        return Task.Run(() =>
        {
            var found = remembered is not null && GuestToolsDownload.IsDriverDisc(remembered) ? remembered : GuestToolsDownload.Existing(paths);
            Ui.Post(() => HasGuestTools = found is not null);
            return found;
        });
    }

    /// <summary>The guest tools disc: found, or downloaded now with progress here (and in <paramref name="also"/>). <c>null</c> = cancelled or failed (the person was told).</summary>
    public Task<string?> GetGuestToolsAsync(IProgress<DownloadProgress>? also = null)
    {
        _guestToolsAlso = also;
        if (_guestToolsTask is { IsCompleted: false } running) return running;
        _guestToolsCancel = new CancellationTokenSource();
        var token = _guestToolsCancel.Token;
        _guestToolsTask = Task.Run(async () =>
        {
            if (await FindGuestToolsAsync().ConfigureAwait(false) is { } found) return found;
            Ui.Post(() =>
            {
                GuestToolsBusy = true;
                GuestToolsStatus = "Finding the newest guest tools…";
            });
            var progress = new Progress<DownloadProgress>(p =>
            {
                _guestToolsAlso?.Report(p);
                Ui.Post(() =>
                {
                    GuestToolsSizeKnown = p.ExpectedBytes is > 0;
                    GuestToolsPercent = p.ExpectedBytes is { } e and > 0 ? Math.Min(100, p.Bytes * 100.0 / e) : 0;
                    GuestToolsStatus = p.ExpectedBytes is { } size and > 0 ? $"{p.Status} {Formats.Bytes(p.Bytes)} of {Formats.Bytes(size)}" : p.Status;
                });
            });
            try
            {
                var path = await GuestToolsDownload.GetAsync(Paths, progress, token).ConfigureAwait(false);
                RememberDriversIso(path);
                Ui.Post(() => HasGuestTools = true);
                return path;
            }
            catch (DownloadFailedException ex)
            {
                Ui.Post(() => Show(new NoticeViewModel("The guest tools did not download", ex.Reasons, isError: true)));
                return null;
            }
            catch (OperationCanceledException)
            {
                Ui.Post(() => Note("The guest tools download was stopped; it carries on from there next time."));
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
            {
                Ui.Post(() => Show(new NoticeViewModel("The guest tools did not download", [ex.Message], isError: true)));
                return null;
            }
            finally
            {
                Ui.Post(() => GuestToolsBusy = false);
            }
        }, CancellationToken.None);
        return _guestToolsTask;
    }

    [RelayCommand]
    private void CancelGuestTools() => _guestToolsCancel?.Cancel();
}
