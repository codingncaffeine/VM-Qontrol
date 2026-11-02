using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;

namespace VmQontrol.App.ViewModels;

/// <summary>One command the palette offers: its words, where it applies, and what it does.</summary>
public sealed record PaletteItem(string Title, string Context, Action Run);

/// <summary>
/// Ctrl+K: every action in one search box. Words match in any order, each as the start of a word or anywhere; earlier
/// and whole-word matches rank first. Enter runs the highlighted command.
/// </summary>
public sealed partial class PaletteViewModel(ShellViewModel shell) : ObservableObject
{
    private List<PaletteItem> _all = [];

    public ObservableCollection<PaletteItem> Items { get; } = [];

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _query = "";
    [ObservableProperty] private PaletteItem? _highlighted;

    public void Open()
    {
        _all = Build();
        Query = "";
        Filter();
        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    public void RunHighlighted()
    {
        var item = Highlighted;
        Close();
        item?.Run();
    }

    public void Move(int delta)
    {
        if (Items.Count == 0) return;
        var index = Highlighted is null ? -1 : Items.IndexOf(Highlighted);
        Highlighted = Items[Math.Clamp(index + delta, 0, Items.Count - 1)];
    }

    partial void OnQueryChanged(string value) => Filter();

    private void Filter()
    {
        var words = Query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var ranked = _all.Select(i => (Item: i, Score: Score(i, words))).Where(x => x.Score >= 0).OrderBy(x => x.Score).ThenBy(x => x.Item.Title, StringComparer.CurrentCultureIgnoreCase);
        Items.Clear();
        foreach (var (item, _) in ranked.Take(40)) Items.Add(item);
        Highlighted = Items.FirstOrDefault();
    }

    /// <summary>Lower is better; -1 = no match.</summary>
    public static int Score(PaletteItem item, IReadOnlyList<string> words)
    {
        var text = $"{item.Title} {item.Context}";
        var score = 0;
        foreach (var w in words)
        {
            var at = text.IndexOf(w, StringComparison.CurrentCultureIgnoreCase);
            if (at < 0) return -1;
            var wordStart = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            score += at + (wordStart ? 0 : 50);
        }

        return score;
    }

    private List<PaletteItem> Build()
    {
        var items = new List<PaletteItem>
        {
            new("New machine", "Create", shell.NewMachine),
            new("Refresh the library", "Library", () => _ = shell.RefreshAsync()),
            new("Show the host check", "Welcome", () => shell.Selected = null),
        };
        foreach (var m in shell.Machines) items.Add(new($"Go to {m.Name}", m.GuestLabel, () => shell.Selected = m));
        if (shell.Selected is { } s)
        {
            void Add(string title, ICommand command, bool when = true)
            {
                if (when) items.Add(new(title, s.Name, () => command.Execute(null)));
            }

            Add("Power on", s.PowerOnCommand, s.IsOff);
            Add("Open screen", s.OpenScreenCommand, s.IsActive);
            Add("Shut down", s.ShutDownCommand, s.IsRunning);
            Add("Pause", s.PauseCommand, s.CanPause);
            Add("Resume", s.ResumeCommand, s.IsPaused);
            Add("Reset", s.ResetCommand, s.IsActive);
            Add("Power off", s.PowerOffCommand, s.IsActive);
            Add("Open folder", s.OpenFolderCommand);
            Add("Copy command line", s.CopyCommandLineCommand);
            Add(s.IsFavourite ? "Remove from favourites" : "Add to favourites", s.ToggleFavouriteCommand);
            items.Add(new("Show summary", s.Name, () => s.Tab = 0));
            items.Add(new("Show performance", s.Name, () => s.Tab = 1));
            items.Add(new("Show console", s.Name, () => s.Tab = 2));
            items.Add(new("Show log", s.Name, () => s.Tab = 3));
        }

        return items;
    }
}
