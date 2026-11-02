using CommunityToolkit.Mvvm.ComponentModel;
using VmQontrol.Core.Hosts;

namespace VmQontrol.App.ViewModels;

public sealed record CheckRow(string Title, string Detail, string? Fix, CheckStatus Status)
{
    public bool HasFix => Fix is not null;

    public string Badge => Status switch
    {
        CheckStatus.Ok => "Ready",
        CheckStatus.Optional => "Optional",
        CheckStatus.Warning => "Attention",
        _ => "Missing",
    };

    public bool IsOk => Status == CheckStatus.Ok;

    public bool IsOptional => Status == CheckStatus.Optional;

    public bool IsWarning => Status == CheckStatus.Warning;

    public bool IsMissing => Status == CheckStatus.Missing;
}

/// <summary>The first page: what this computer is ready for, and where to start.</summary>
public sealed partial class WelcomeViewModel : ObservableObject
{
    [ObservableProperty]
    private IReadOnlyList<CheckRow> _checks = [];

    [ObservableProperty]
    private string _summary = "Checking this computer…";

    [ObservableProperty]
    private bool _checked;

    public void Show(IReadOnlyList<DoctorCheck> checks)
    {
        Checks = checks.Select(c => new CheckRow(c.Title, c.Detail, c.Fix, c.Status)).OrderByDescending(r => r.Status).ToList();
        var missing = checks.Count(c => c.Status == CheckStatus.Missing);
        var warnings = checks.Count(c => c.Status == CheckStatus.Warning);
        Summary = missing > 0
            ? $"{missing} thing{(missing == 1 ? " is" : "s are")} missing before machines can run. Each has the command that installs it."
            : warnings > 0
                ? "Machines can run, but something needs your attention."
                : "This computer is ready to run virtual machines.";
        Checked = true;
    }
}
