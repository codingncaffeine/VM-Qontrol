using System.Text;

namespace VmQontrol.Core.Runtime;

/// <summary>
/// remote-viewer (virt-viewer 11) draws its window's buttons as bare icons: of the six at the top only "Change CD" says
/// what it does. GLib lets the program that starts it lay a changed copy of one of its built-in files over the original
/// (<c>G_RESOURCE_OVERLAYS</c>), so VM Qontrol takes the installed viewer's own window file, adds a tooltip to every
/// button that has none, and hands the copy over at start. Only tooltips are added; when the file is not where this
/// version keeps it, or anything else is unexpected, the viewer starts unchanged.
/// </summary>
public static class ViewerTooltips
{
    public const string Variable = "G_RESOURCE_OVERLAYS";
    public const string ResourcePath = "/org/virt-manager/virt-viewer/ui/virt-viewer.ui";

    /// <summary>The window's button ids (the windowed bar is "header-", the full-screen bar "toolbar-") and what each says.</summary>
    public static readonly IReadOnlyList<(string Id, string Tip)> Tips =
    [
        ("header-action", "Menu: screenshot, zoom, displays, preferences, guest details"),
        ("header-send-key", "Send a key combination this computer would otherwise keep, such as Ctrl+Alt+Del"),
        ("header-usb", "USB devices: choose which ones go to the machine"),
        ("header-cd", "Change CD"),
        ("header-machine", "The machine: pause, reset or power down"),
        ("header-fullscreen", "Full screen (F11)"),
        ("toolbar-action", "Menu: screenshot, zoom, displays, preferences, guest details"),
        ("toolbar-send-key", "Send a key combination this computer would otherwise keep, such as Ctrl+Alt+Del"),
        ("toolbar-usb", "USB devices: choose which ones go to the machine"),
        ("toolbar-cd", "Change CD"),
        ("toolbar-machine", "The machine: pause, reset or power down"),
        ("toolbar-fullscreen", "Leave full screen (F11)"),
    ];

    /// <summary>
    /// The environment value that gives <paramref name="program"/> (remote-viewer) its buttons' tooltips, writing the
    /// changed window file under <paramref name="cacheDirectory"/> once per installed viewer; <c>null</c> when it cannot.
    /// </summary>
    public static string? Overlay(string program, string cacheDirectory)
    {
        try
        {
            var info = new FileInfo(program);
            if (!info.Exists) return null;
            var dir = Path.Combine(cacheDirectory, "viewer");
            var file = Path.Combine(dir, "virt-viewer.ui");
            var stamp = Path.Combine(dir, "virt-viewer.stamp");
            var key = $"{info.FullName}\n{info.Length}\n{info.LastWriteTimeUtc.Ticks}\n{Tips.Count}";
            if (!(File.Exists(file) && File.Exists(stamp) && File.ReadAllText(stamp) == key))
            {
                if (Patch(Extract(File.ReadAllBytes(program))) is not { } patched)
                {
                    Log.Info($"{Path.GetFileName(program)}: its window file was not found where expected; it starts without added tooltips.");
                    return null;
                }

                Directory.CreateDirectory(dir);
                File.WriteAllText(file, patched);
                File.WriteAllText(stamp, key);
            }

            return $"{ResourcePath}={file}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Viewer tooltips: {ex.Message}");
            return null;
        }
    }

    /// <summary>The window file as built into the program: the XML document holding the "header" bar (stored uncompressed).</summary>
    public static string? Extract(byte[] binary)
    {
        var anchor = binary.AsSpan().IndexOf("<object class=\"GtkHeaderBar\" id=\"header\">"u8);
        if (anchor < 0) return null;
        var start = binary.AsSpan(0, anchor).LastIndexOf("<?xml"u8);
        var end = binary.AsSpan(anchor).IndexOf("</interface>"u8);
        if (start < 0 || end < 0) return null;
        var length = anchor + end + "</interface>".Length - start;
        var text = Encoding.UTF8.GetString(binary, start, length);
        // A NUL inside means two neighbouring resources were joined: not the file.
        return text.Contains('\0') ? null : text + "\n";
    }

    /// <summary>Adds a tooltip-text property to each known button that has none; <c>null</c> unless every known button was found.</summary>
    public static string? Patch(string? ui)
    {
        if (ui is null) return null;
        var text = ui;
        foreach (var (id, tip) in Tips)
        {
            var open = text.IndexOf($" id=\"{id}\">", StringComparison.Ordinal);
            if (open < 0) return null;
            var lineEnd = text.IndexOf('\n', open);
            if (lineEnd < 0) return null;
            // This object's own properties come before its first child.
            var child = text.IndexOf("<child", lineEnd, StringComparison.Ordinal);
            var own = child < 0 ? text[lineEnd..] : text[lineEnd..child];
            if (own.Contains("name=\"tooltip-text\"", StringComparison.Ordinal)) continue;
            var indent = new string(' ', LeadingSpaces(text, open) + 2);
            text = text.Insert(lineEnd + 1, $"{indent}<property name=\"tooltip-text\">{System.Security.SecurityElement.Escape(tip)}</property>\n");
        }

        return text;
    }

    private static int LeadingSpaces(string text, int at)
    {
        var lineStart = text.LastIndexOf('\n', at) + 1;
        var n = 0;
        while (lineStart + n < text.Length && text[lineStart + n] == ' ') n++;
        return n;
    }
}
