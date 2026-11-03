using System.Xml.Linq;
using VmQontrol.Core.Runtime;

namespace VmQontrol.Tests;

/// <summary>remote-viewer's icon buttons get tooltips from an overlay of its own window file (the user found them silent).</summary>
public sealed class ViewerTooltipsTests
{
    private const string Sample = """
        <?xml version="1.0" encoding="UTF-8"?>
        <interface>
          <object class="GtkHeaderBar" id="header">
            <child>
              <object class="GtkButton" id="header-usb">
                <property name="visible">True</property>
                <child>
                  <object class="GtkImage"><property name="tooltip-text">not the button's</property></object>
                </child>
              </object>
            </child>
            <child>
              <object class="GtkButton" id="header-cd">
                <property name="tooltip-text" translatable="yes">Change CD</property>
              </object>
            </child>
          </object>
        </interface>
        """;

    [Fact]
    public void A_button_without_a_tooltip_gets_one_and_one_with_a_tooltip_keeps_its_own()
    {
        // Only the two ids the sample has: the rest are absent, so the whole patch refuses.
        Assert.Null(ViewerTooltips.Patch(Sample));
        var all = Sample.Replace("</interface>", string.Concat(ViewerTooltips.Tips.Where(t => t.Id is not ("header-usb" or "header-cd"))
            .Select(t => $"  <object class=\"GtkButton\" id=\"{t.Id}\">\n  </object>\n")) + "</interface>");
        var doc = XDocument.Parse(ViewerTooltips.Patch(all)!);
        string? Tip(string id) => doc.Descendants("object").Single(o => (string?)o.Attribute("id") == id)
            .Elements("property").SingleOrDefault(p => (string?)p.Attribute("name") == "tooltip-text")?.Value;
        Assert.Equal("USB devices: choose which ones go to the machine", Tip("header-usb"));
        Assert.Equal("Change CD", Tip("header-cd"));
        foreach (var (id, tip) in ViewerTooltips.Tips) Assert.Equal(tip, Tip(id));
    }

    [Fact]
    [Trait("Tier", "Integration")]
    public void The_installed_remote_viewer_gets_a_tooltip_on_every_button()
    {
        const string program = "/usr/bin/remote-viewer";
        Assert.SkipUnless(File.Exists(program), "remote-viewer is not installed here");
        var cache = Directory.CreateTempSubdirectory("vmq-viewer-tips").FullName;
        try
        {
            var overlay = ViewerTooltips.Overlay(program, cache);
            Assert.NotNull(overlay);
            Assert.StartsWith(ViewerTooltips.ResourcePath + "=", overlay);
            var doc = XDocument.Load(overlay[(ViewerTooltips.ResourcePath.Length + 1)..]);
            var buttons = doc.Descendants("object").Where(o => ((string?)o.Attribute("class")) is "GtkButton" or "GtkMenuButton").ToList();
            Assert.Equal(12, buttons.Count);
            foreach (var b in buttons)
                Assert.True(b.Elements("property").Any(p => (string?)p.Attribute("name") == "tooltip-text"), $"{b.Attribute("id")} has no tooltip");
            // The original file, apart from the added lines.
            var original = ViewerTooltips.Extract(File.ReadAllBytes(program))!;
            var added = File.ReadAllLines(overlay[(ViewerTooltips.ResourcePath.Length + 1)..]).Except(original.Split('\n')).ToList();
            Assert.All(added, l => Assert.Contains("name=\"tooltip-text\"", l));
            Assert.Equal(10, added.Count);
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }
}
