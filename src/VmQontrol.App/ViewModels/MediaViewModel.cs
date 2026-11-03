using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VmQontrol.Core.Downloads;
using VmQontrol.Core.Library;

namespace VmQontrol.App.ViewModels;

/// <summary>One disk or disc as the list shows it.</summary>
public sealed class LibraryMediaRow(MediaItem item)
{
    public MediaItem Item { get; } = item;

    public string Name => Path.GetFileName(Item.Path);

    public string Folder => Path.GetDirectoryName(Item.Path) ?? "";

    public bool IsUnused => Item.Status == MediaStatus.Unused;

    public bool IsMissing => Item.Status == MediaStatus.Missing;

    public bool IsInUse => Item.Status == MediaStatus.InUse;

    public bool IsDisc => Item.Kind == MediaKind.Disc;

    public string StatusText => Item.Status switch { MediaStatus.Unused => "Unused", MediaStatus.Missing => "Missing", _ => "In use" };

    public string KindText => Item.Kind switch
    {
        MediaKind.Disc => "Disc",
        MediaKind.Base => "Shared base disk",
        _ => Item.Format.Length > 0 ? $"Disk ({Item.Format})" : "Disk",
    };

    /// <summary>"64 GB disk, 3.1 GB on the drive": what the guest sees, and what it takes here.</summary>
    public string SizeText => Item.Status == MediaStatus.Missing ? "Not on the drive"
        : Item.Kind == MediaKind.Disc ? Formats.Bytes(Item.ActualBytes ?? 0)
        : Item.VirtualBytes is { } v ? $"{Formats.Bytes(v)} disk, {Formats.Bytes(Item.ActualBytes ?? 0)} on the drive"
        : $"{Formats.Bytes(Item.ActualBytes ?? 0)} on the drive";

    public string UsedByText => Item.UsedBy.Count == 0 ? "No machine uses it" : "Used by " + string.Join(", ", Item.UsedBy);
}

/// <summary>
/// Disks and discs (phase 19 item 7, VirtualBox's Virtual Media Manager): every image the library holds, its sizes and
/// users, filtered by kind; unused ones can be deleted (asked first) to free the drive. Scans on a worker.
/// </summary>
public sealed partial class MediaViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private IReadOnlyList<LibraryMediaRow> _rows = [];

    public MediaViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _ = RefreshAsync();
    }

    public event Action? Closed;

    public static IReadOnlyList<Option> FilterOptions { get; } =
    [
        new("all", "Everything"),
        new("disk", "Disks"),
        new("disc", "Discs"),
        new("unused", "Unused"),
    ];

    [ObservableProperty]
    private Option _filter = FilterOptions[0];

    [ObservableProperty]
    private bool _loading = true;

    [ObservableProperty]
    private string _summary = "Looking through the library…";

    public ObservableCollection<LibraryMediaRow> Shown { get; } = [];

    public bool Empty => !Loading && Shown.Count == 0;

    partial void OnFilterChanged(Option value) => ApplyFilter();

    private void ApplyFilter()
    {
        Shown.Clear();
        foreach (var r in _rows.Where(r => Filter.Value switch
                 {
                     "disk" => r.Item.Kind != MediaKind.Disc,
                     "disc" => r.Item.Kind == MediaKind.Disc,
                     "unused" => r.IsUnused,
                     _ => true,
                 }))
            Shown.Add(r);
        OnPropertyChanged(nameof(Empty));
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        Loading = true;
        var roots = _shell.LibraryRoots.ToList();
        var paths = _shell.Paths;
        var drivers = _shell.Settings.DriversIso;
        var items = await Task.Run(async () =>
        {
            var entries = roots.SelectMany(r => LibraryScanner.Scan(r)).ToList();
            var shared = new List<string>();
            if (GuestToolsDownload.Existing(paths) is { } tools) shared.Add(tools);
            if (drivers is not null) shared.Add(drivers);
            return await MediaInventory.ScanAsync(entries, roots, shared).ConfigureAwait(false);
        }).ConfigureAwait(false);
        Ui.Post(() =>
        {
            _rows = items.Select(i => new LibraryMediaRow(i)).ToList();
            var onDrive = items.Where(i => i.Status != MediaStatus.Missing).Sum(i => i.ActualBytes ?? 0);
            var unused = items.Where(i => i.Status == MediaStatus.Unused).ToList();
            var missing = items.Count(i => i.Status == MediaStatus.Missing);
            static string Count(int n, string one) => $"{n} {one}{(n == 1 ? "" : "s")}";
            var disks = items.Count(i => i.Kind != MediaKind.Disc);
            var discs = items.Count(i => i.Kind == MediaKind.Disc);
            var what = discs == 0 ? Count(disks, "disk") : disks == 0 ? Count(discs, "disc") : $"{Count(disks, "disk")} and {Count(discs, "disc")}";
            Summary = items.Count == 0 ? "No disks or discs in the library yet." : $"{what} take{(items.Count == 1 ? "s" : "")} {Formats.Bytes(onDrive)} on the drive."
                      + (items.Count == 0 ? "" : unused.Count > 0 ? $" {unused.Count} unused ({Formats.Bytes(unused.Sum(i => i.ActualBytes ?? 0))}) can be deleted." : " Every one is in use.")
                      + (missing > 0 ? $" {missing} named by a machine {(missing == 1 ? "is" : "are")} missing." : "");
            Loading = false;
            ApplyFilter();
        });
    }

    [RelayCommand]
    private static void ShowInFolder(LibraryMediaRow row) => Platform.Links.Open(row.Folder);

    [RelayCommand]
    private async Task DeleteAsync(LibraryMediaRow row)
    {
        if (!row.IsUnused) return;
        var notice = new NoticeViewModel($"Delete {row.Name}?",
            [$"No machine uses it; deleting frees {Formats.Bytes(row.Item.ActualBytes ?? 0)} on the drive. It cannot be undone.", row.Item.Path],
            confirm: "Delete", isDanger: true);
        _shell.Show(notice);
        if (!await notice.Answer.ConfigureAwait(false)) return;
        try
        {
            await Task.Run(() => File.Delete(row.Item.Path)).ConfigureAwait(false);
            Ui.Post(() => _shell.Note($"{row.Name} was deleted."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Ui.Post(() => _shell.Show(new NoticeViewModel($"{row.Name} could not be deleted", [ex.Message], isError: true)));
        }

        await RefreshAsync().ConfigureAwait(false);
    }

    [RelayCommand]
    private void Close() => Closed?.Invoke();
}
