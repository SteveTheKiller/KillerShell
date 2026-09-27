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
        Assert.Equal("160", (string?)tab.Attribute("MinWidth"));
        Assert.Equal("260", (string?)tab.Attribute("MaxWidth"));

        XElement grain = document.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "tabGrain");
        Assert.Equal("{DynamicResource TabGrainMargin}", (string?)grain.Attribute("Margin"));

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

        Assert.Equal("#c0c0c0", BrushColor("TabActiveBrush"), ignoreCase: true);
        Assert.Equal("#9f9f9f", BrushColor("TabInactiveBrush"), ignoreCase: true);
        Assert.Equal("#c0c0c0", BrushColor("FocusedPaneBrush"), ignoreCase: true);

        XElement join = document.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "RetroTabJoinLine");
        Assert.Equal("{DynamicResource PaneBorderBrush}", (string?)join.Attribute("Background"));
        Assert.Equal("0,0,0,1", (string?)join.Attribute("Margin"));

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

        foreach (string outlineName in new[] { "tabActiveRetroOuterOutline", "tabInactiveRetroOuterOutline" })
        {
            XElement outline = document.Descendants()
                .Single(element => (string?)element.Attribute(x + "Name") == outlineName);
            Assert.Equal("{DynamicResource PaneBorderBrush}", (string?)outline.Attribute("BorderBrush"));
            Assert.Equal("1,1,1,0", (string?)outline.Attribute("BorderThickness"));
        }

        XElement inactiveTrigger = document.Descendants()
            .Where(element => element.Name.LocalName == "MultiDataTrigger")
            .Single(element => element.Descendants().Any(condition =>
                    condition.Name.LocalName == "Condition"
                    && (string?)condition.Attribute("Binding") == "{Binding UseRetroTabChrome}"
                    && (string?)condition.Attribute("Value") == "True")
                && element.Descendants().Any(condition =>
                    condition.Name.LocalName == "Condition"
                    && (string?)condition.Attribute("Binding") == "{Binding IsActive}"
                    && (string?)condition.Attribute("Value") == "False")
                && element.Elements().Any(setter =>
                    setter.Name.LocalName == "Setter"
                    && (string?)setter.Attribute("TargetName") == "tabBd"
                    && (string?)setter.Attribute("Property") == "Background"));
        Assert.Contains(document.Descendants().Where(element => element.Name.LocalName == "MultiDataTrigger").SelectMany(element => element.Elements()), setter =>
            setter.Name.LocalName == "Setter"
            && (string?)setter.Attribute("TargetName") == "tabInactiveRetroOuterOutline"
            && (string?)setter.Attribute("Property") == "Visibility"
            && (string?)setter.Attribute("Value") == "Visible");
        XElement closeOffset = inactiveTrigger.Elements()
            .Single(setter => setter.Name.LocalName == "Setter"
                && (string?)setter.Attribute("TargetName") == "tabClose"
                && (string?)setter.Attribute("Property") == "RenderTransform");
        XElement translate = closeOffset.Descendants()
            .Single(element => element.Name.LocalName == "TranslateTransform");
        Assert.Equal("-2", (string?)translate.Attribute("Y"));

        string ThicknessValue(string key) => theme.Descendants()
            .Single(element => element.Name.LocalName == "Thickness" &&
                (string?)element.Attribute(x + "Key") == key).Value;
        Assert.Equal("0,-2,0,0", ThicknessValue("PaneOuterMargin"));
        Assert.Equal("8,6,5,2", ThicknessValue("TabPadding"));
        Assert.Equal("8,4,5,4", ThicknessValue("TabActivePadding"));
        Assert.Equal("0,5,0,1", ThicknessValue("TabMargin"));
        Assert.Equal("0,5,0,1", ThicknessValue("TabInactiveFirstMargin"));
        Assert.Equal("0,5,1,1", ThicknessValue("TabInactiveLastMargin"));
        Assert.Equal("0,3,0,0", ThicknessValue("TabActiveMargin"));
        Assert.Equal("0,3,0,0", ThicknessValue("TabActiveFirstMargin"));
        Assert.Equal("0,3,1,0", ThicknessValue("TabActiveLastMargin"));
        Assert.Equal("0,3,1,0", ThicknessValue("TabActiveOnlyMargin"));
        Assert.Equal("-8,-4,-6,0", ThicknessValue("TabActiveOuterOutlineMargin"));
        Assert.Equal("-8,-5,-6,0", ThicknessValue("TabInactiveOuterOutlineMargin"));
        Assert.Equal("-8,-4,-5,0", ThicknessValue("TabActiveBevelLightMargin"));
        Assert.Equal("0", ThicknessValue("PaneBevel2LightThickness"));
        Assert.Equal("0", ThicknessValue("PaneBevel2DarkThickness"));
        Assert.Equal("1,1,0,0", ThicknessValue("BarEdgeThickness"));
        Assert.Equal("1,0,0,0", ThicknessValue("LocationBarEdgeThickness"));
        Assert.Equal("0,0,1,1", ThicknessValue("BarEdgeDarkThickness"));
    }

    [Fact]
    public void NormalTabsKeepCompactSpacingAndBothActiveSideBorders()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "Services", "ThemeManager.cs"));

        Assert.Contains("SetIfAbsent(\"TabBarMargin\", new Thickness(0, 0, 8, 0));", source, StringComparison.Ordinal);
        Assert.Contains("combined[\"TabStripeThickness\"] = flat ? new Thickness(0) : new Thickness(1, 3, 1, 0);", source, StringComparison.Ordinal);
        Assert.Contains(": new Thickness(11, 1, 4, 5);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RetroContentPaneUsesOneOuterLineAndOneInnerHighlight()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "Shell", "Tabs.cs"));

        Assert.Contains("Pane.ResultsPane.BorderThickness = new Thickness(1, 0, 1, 1);", source, StringComparison.Ordinal);
        Assert.Contains("Pane.PaneBevelOuterDark.Margin = new Thickness(1, 0, 1, 1);", source, StringComparison.Ordinal);
        Assert.Contains("Pane.PaneBevelOuterDark.BorderThickness = new Thickness(1, 0, 0, 0);", source, StringComparison.Ordinal);
        Assert.Contains("Pane.PaneBevelOuterLight.BorderThickness = new Thickness(0);", source, StringComparison.Ordinal);
        Assert.Contains("Pane.PaneBevelInnerDark.BorderThickness = new Thickness(0);", source, StringComparison.Ordinal);
        Assert.Contains("Pane.PaneBevelInnerLight.BorderThickness = new Thickness(0);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void VerticalTabDragCanStartATearOut()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "Shell", "Tabs.cs"));

        Assert.Contains("MinimumHorizontalDragDistance", source, StringComparison.Ordinal);
        Assert.Contains("MinimumVerticalDragDistance", source, StringComparison.Ordinal);
        Assert.Contains("OutsideWindow(e)", source, StringComparison.Ordinal);
        Assert.Contains("TearOutTab(t)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DragGhostUsesTheActiveTabThemeResources()
    {
        string root = FindRepositoryRoot();
        var window = XDocument.Load(Path.Combine(root, "Shell", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement ghost = window.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "TabDragGhost");

        Assert.Equal("{DynamicResource TabCornerRadius}", (string?)ghost.Attribute("CornerRadius"));
        Assert.Equal("{DynamicResource TabActiveBrush}", (string?)ghost.Attribute("Background"));
        Assert.Equal("{DynamicResource TabActivePadding}", (string?)ghost.Attribute("Padding"));
        Assert.Equal("{DynamicResource BarShadowEffect}", (string?)ghost.Attribute("Effect"));
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
