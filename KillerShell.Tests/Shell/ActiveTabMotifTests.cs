using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace KillerShell.Tests.Shell;

/// <summary>
/// Trash Polka: the active tab and the bar under it must paint the same window-pinned picture, or the tab shows a
/// different crop and a seam runs along its foot. The bars used to fall to the popup piece, drawn from each bar's own
/// corner, and the tab borrowed the bar's brush stretched over its own bounds.
/// </summary>
public sealed class ActiveTabMotifTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string Kc = "{clr-namespace:KillerShell.Controls}";

    [Theory]
    [InlineData("LocationRow")]
    [InlineData("TerminalBarRow")]
    [InlineData("EditorBarRow")]
    public void TabAndBarsShareTheWindowPinnedPaneInk(string name)
    {
        var el = Named(name);
        Assert.Equal("Window", (string?)el.Attribute(Kc + "ThemeMotif.Part"));
        Assert.Equal("MenuBackgroundBrush", (string?)el.Attribute(Kc + "ThemeMotif.BaseBrush"));
    }

    // The active tab joins a bar from a top row but the page itself from a bottom row, so its ink takes the surface
    // PaneBars picks for the join instead of one fixed brush.
    [Fact]
    public void TabInkPaintsTheSurfaceItJoins()
    {
        var ink = Named("tabInk");
        Assert.Equal("Window", (string?)ink.Attribute(Kc + "ThemeMotif.Part"));
        Assert.StartsWith("{Binding ActiveTabInkBrush", (string?)ink.Attribute(Kc + "ThemeMotif.BaseBrush"));
        Assert.StartsWith("{Binding ActiveTabInkScreen", (string?)ink.Attribute(Kc + "ThemeMotif.Screen"));
    }

    [Fact]
    public void TabInkHasNoBackgroundAndShowsOnlyOnTheActiveTab()
    {
        var ink = Named("tabInk");
        Assert.Null(ink.Attribute("Background"));
        Assert.Equal("Collapsed", (string?)ink.Attribute("Visibility"));
        Assert.Contains(Pane().Descendants().Where(e => e.Name.LocalName == "DataTrigger"
                && (string?)e.Attribute("Binding") == "{Binding IsActive}" && (string?)e.Attribute("Value") == "True")
            .SelectMany(t => t.Elements()),
            s => (string?)s.Attribute("TargetName") == "tabInk" && (string?)s.Attribute("Property") == "Visibility"
                && (string?)s.Attribute("Value") == "Visible");
    }

    private static XElement Named(string name) =>
        Pane().Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == name);

    private static XDocument Pane() => XDocument.Load(Path.Combine(RepoRoot(), "Controls", "FilePane.xaml"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Controls", "FilePane.xaml"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("KillerShell repo root");
    }
}
