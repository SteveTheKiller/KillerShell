using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace KillerShell.Tests;

public sealed class TabLayoutTests
{
    [Fact]
    public void VisibleTabsUseBoundedLeftAlignedWidth()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "Shell", "Tabs.cs"));
        var document = XDocument.Load(Path.Combine(root, "Controls", "FilePane.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.Contains("int visibleCount = overflow ? cap : n;", source, StringComparison.Ordinal);
        Assert.Contains("visibleCount * TabCeilingWidth", source, StringComparison.Ordinal);

        XElement host = document.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "TabStripHost");
        Assert.Equal("Left", (string?)host.Attribute("HorizontalAlignment"));

        XElement tab = document.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "tabBd");
        Assert.Equal("120", (string?)tab.Attribute("MinWidth"));
        Assert.Equal("240", (string?)tab.Attribute("MaxWidth"));

        Assert.DoesNotContain(document.Descendants(), element =>
            (string?)element.Attribute(x + "Name") is "TabEdgeLeft" or "TabEdgeRight" or "TabBarRing" or "tabSeamPatch");
    }

    [Fact]
    public void RetroTabsHaveContinuousPaneJoin()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "Shell", "Tabs.cs"));
        var document = XDocument.Load(Path.Combine(root, "Controls", "FilePane.xaml"));
        var theme = XDocument.Load(Path.Combine(root, "Themes", "98SE.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        string BrushColor(string key) => (string)theme.Descendants()
            .Single(element => element.Name.LocalName == "SolidColorBrush" &&
                (string?)element.Attribute(x + "Key") == key)
            .Attribute("Color")!;

        Assert.Equal("#9f9f9f", BrushColor("TabActiveBrush"), ignoreCase: true);
        Assert.Equal("#c0c0c0", BrushColor("TabInactiveBrush"), ignoreCase: true);

        XElement join = document.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "RetroTabJoinLine");
        Assert.Equal("{DynamicResource PaneBorderBrush}", (string?)join.Attribute("Background"));

        XElement innerJoin = document.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "RetroTabInnerJoin");
        Assert.Equal("1,0,2,-1", (string?)innerJoin.Attribute("Margin"));
        foreach (string segmentName in new[] { "RetroTabInnerJoinLeft", "RetroTabInnerJoinRight" })
        {
            XElement segment = document.Descendants()
                .Single(element => (string?)element.Attribute(x + "Name") == segmentName);
            Assert.Equal("{DynamicResource BevelLightBrush}", (string?)segment.Attribute("Background"));
        }

        Assert.Contains("UpdateRetroTabInnerJoin", source, StringComparison.Ordinal);
        Assert.Contains("Canvas.SetLeft(pane.RetroTabInnerJoinRight, activeRight)", source, StringComparison.Ordinal);

        XElement outline = document.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "tabActiveRetroOuterOutline");
        Assert.Equal("{DynamicResource PaneBorderBrush}", (string?)outline.Attribute("BorderBrush"));
        Assert.Equal("1,1,1,0", (string?)outline.Attribute("BorderThickness"));

        string ThicknessValue(string key) => theme.Descendants()
            .Single(element => element.Name.LocalName == "Thickness" &&
                (string?)element.Attribute(x + "Key") == key).Value;
        Assert.Equal("-13,-5,-6,-2", ThicknessValue("TabActiveOuterOutlineMargin"));
        Assert.Equal("0,3,0,-3", ThicknessValue("TabActiveMargin"));
        Assert.Equal("1,3,0,-3", ThicknessValue("TabActiveFirstMargin"));
        Assert.Equal("0,3,1,-3", ThicknessValue("TabActiveLastMargin"));
        Assert.Equal("1,3,1,-3", ThicknessValue("TabActiveOnlyMargin"));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KillerShell.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the KillerShell repository root.");
    }
}
