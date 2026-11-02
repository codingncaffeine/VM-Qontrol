using Avalonia.Controls;
using VmQontrol.App.ViewModels;

namespace VmQontrol.App.Views;

public partial class MachineView : UserControl
{
    private ConsoleViewModel? _console;

    public MachineView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_console is not null) _console.OutputAdded -= OnOutput;
            _console = (DataContext as MachineViewModel)?.Console;
            if (_console is not null) _console.OutputAdded += OnOutput;
        };
    }

    /// <summary>New console output keeps the view at the bottom, unless the user has scrolled up to read.</summary>
    private void OnOutput()
    {
        if (this.FindControl<ListBox>("ConsoleLines") is not { Scroll: ScrollViewer scroll } list || _console is null) return;
        var atBottom = scroll.Offset.Y >= scroll.Extent.Height - scroll.Viewport.Height - 40;
        if (atBottom && _console.Lines.Count > 0) list.ScrollIntoView(_console.Lines.Count - 1);
    }
}
