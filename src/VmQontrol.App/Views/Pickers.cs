using Avalonia.Controls;
using Avalonia.Platform.Storage;
using VmQontrol.Core;

namespace VmQontrol.App.Views;

/// <summary>
/// The system's folder and file dialogs (on Linux, the desktop's portal), with every step logged: a dialog that never
/// opens otherwise leaves no trace (found 2026-11-02: "Add machines from a folder…" did nothing and the log was empty).
/// </summary>
internal static class Pickers
{
    public static async Task<string?> FolderAsync(Control from, string title)
    {
        if (TopLevel.GetTopLevel(from) is not { } top)
        {
            Log.Warn($"Folder dialog \"{title}\": the view is not in a window.");
            return null;
        }

        var storage = top.StorageProvider;
        Log.Info($"Folder dialog \"{title}\": opening ({storage.GetType().Name}, can pick folders: {storage.CanPickFolder}).");
        try
        {
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title });
            var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            Log.Info(folders.Count == 0 ? $"Folder dialog \"{title}\": closed without a folder." : $"Folder dialog \"{title}\": {path ?? $"not a local folder ({folders[0].Path})"}.");
            return path;
        }
        catch (Exception ex)
        {
            Log.Error($"Folder dialog \"{title}\" failed.", ex);
            throw new PickerFailedException(ex.Message);
        }
    }
}

/// <summary>The system's dialog could not be shown; the message says why, for the status line.</summary>
internal sealed class PickerFailedException(string message) : Exception(message);
