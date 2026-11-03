namespace VmQontrol.App.Platform;

/// <summary>Opens a web address in the desktop's browser (xdg-open), on a worker; a missing xdg-open is logged, not thrown.</summary>
internal static class Links
{
    public static void Open(string url) => _ = Task.Run(() =>
    {
        try
        {
            using var _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("xdg-open") { ArgumentList = { url }, UseShellExecute = false });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Core.Log.Warn($"{url} could not be opened: {ex.Message}");
        }
    });
}
