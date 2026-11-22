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
        Assert.Equal("{DynamicResource RetroTabJoinLineBrush}", (string?)join.Attribute("Background"));
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
            Assert.Equal("{DynamicResource TabOuterOutlineBrush}", (string?)outline.Attribute("BorderBrush"));
            Assert.Equal("{DynamicResource TabOuterOutlineThickness}", (string?)outline.Attribute("BorderThickness"));
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
                    && (string?)setter.Attribute("Property") == "MenuBackground"));
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
        // 4 above and 4 below centers the title in the tab (041a0a7, the kit's 98SE TabPadding).
        Assert.Equal("8,4,5,4", ThicknessValue("TabPadding"));
        Assert.Equal("8,4,4,4", ThicknessValue("TabActivePadding"));
        Assert.Equal("0,5,0,0", ThicknessValue("TabMargin"));
        Assert.Equal("0,5,0,0", ThicknessValue("TabInactiveFirstMargin"));
        Assert.Equal("0,5,0,0", ThicknessValue("TabInactiveLastMargin"));
        Assert.Equal("-2,3,0,0", ThicknessValue("TabActiveMargin"));
        Assert.Equal("0,3,0,0", ThicknessValue("TabActiveFirstMargin"));
        Assert.Equal("-2,3,0,0", ThicknessValue("TabActiveLastMargin"));
        Assert.Equal("0,3,0,0", ThicknessValue("TabActiveOnlyMargin"));
        Assert.Equal("-8,-4,-5,-4", ThicknessValue("TabActiveOuterOutlineMargin"));
        Assert.Equal("-8,-6,-6,-2", ThicknessValue("TabInactiveOuterOutlineMargin"));
        Assert.Equal("-8,-4,-4,-4", ThicknessValue("TabActiveBevelLightMargin"));
        Assert.Equal("1,1,0,0", ThicknessValue("PaneBevel2LightThickness"));
        Assert.Equal("0,0,1,1", ThicknessValue("PaneBevel2DarkThickness"));
        Assert.Equal("0", ThicknessValue("BarEdgeThickness"));
        Assert.Equal("0", ThicknessValue("LocationBarEdgeThickness"));
        Assert.Equal("0,0,0,1", ThicknessValue("BarEdgeDarkThickness"));
    }

    [Fact]
    public void NormalTabsKeepCompactSpacingAndBothActiveSideBorders()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "Services", "ThemeManager.cs"));

        Assert.Contains("SetIfAbsent(\"TabBarMargin\", new Thickness(0, 0, 8, 0));", source, StringComparison.Ordinal);
        Assert.Contains("combined[\"TabStripeThickness\"] = flat ? new Thickness(0) : new Thickness(1, 3, 1, 0);", source, StringComparison.Ordinal);
        Assert.Contains(": new Thickness(1, 3, 4, 3);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RetroContentPaneIsAWin98PageWithPaddedEdges()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(root, "Shell", "Tabs.cs"));

        Assert.Contains("Pane.ResultsPane.BorderThickness = new Thickness(0);", source, StringComparison.Ordinal);
        Assert.Contains("Pane.ResultsPane.Padding = new Thickness(1, 0, 2, 2);", source, StringComparison.Ordinal);
        Assert.Contains("Pane.PaneBevelOuterDark.Margin = new Thickness(-1, 2, -2, -2);", source, StringComparison.Ordinal);
        Assert.Contains("Pane.PaneBevelOuterDark.BorderThickness = new Thickness(1, 0, 0, 0);", source, StringComparison.Ordinal);
        Assert.Contains("Pane.PaneBevelOuterLight.BorderThickness = new Thickness(0, 0, 1, 1);", source, StringComparison.Ordinal);
        Assert.Contains("Pane.PaneBevelInnerDark.BorderThickness = new Thickness(0);", source, StringComparison.Ordinal);
        Assert.Contains("Pane.PaneBevelInnerLight.BorderThickness = new Thickness(0, 0, 1, 1);", source, StringComparison.Ordinal);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DragGhostRendersTheSourceTabAtItsOriginalSize(bool bottom)
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try { CheckDragGhost(bottom); }
            catch (Exception error) { failure = error; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void CheckDragGhost(bool bottom)
    {
        string root = FindRepositoryRoot();
        var window = XDocument.Load(Path.Combine(root, "Shell", "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement ghost = window.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "TabDragGhost");
        ghost.Name = XName.Get(ghost.Name.LocalName, "clr-namespace:KillerFamily.Windowing;assembly=KillerShell");
        var context = new System.Windows.Markup.ParserContext();
        context.XmlnsDictionary["snap"] = "clr-namespace:KillerFamily.Windowing;assembly=KillerShell";
        var preview = (System.Windows.FrameworkElement)System.Windows.Markup.XamlReader.Parse(ghost.ToString(), context);
        preview.Opacity = 1;
        preview.Effect = null;
        var label = new System.Windows.Controls.TextBlock { Text = "KillerBench", FontSize = 12 };
        var tab = new KillerShell.Controls.TabSurfaceBorder
        {
            Width = 210, Height = 30, CornerRadius = new System.Windows.CornerRadius(3, 3, 0, 0),
            Background = System.Windows.Media.Brushes.DimGray,
            BorderBrush = System.Windows.Media.Brushes.Cyan,
            BorderThickness = new System.Windows.Thickness(1, 3, 1, 0),
            Padding = new System.Windows.Thickness(7, 2, 7, 2), Child = label
        };
        var size = new System.Windows.Size(210, 30);
        if (bottom)
        {
            tab.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            tab.RenderTransform = new System.Windows.Media.ScaleTransform(1, -1);
            label.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            label.RenderTransform = new System.Windows.Media.ScaleTransform(1, -1);
        }
        var container = new System.Windows.Controls.Grid { Width = 210, Height = 30 };
        container.Children.Add(tab);
        container.Measure(size);
        container.Arrange(new System.Windows.Rect(size));
        var livePreview = Assert.IsType<KillerFamily.Windowing.TabDragPreview>(preview);
        livePreview.Source = container;
        preview.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        Assert.Equal(size, preview.DesiredSize);
        preview.Arrange(new System.Windows.Rect(size));
        byte[] Pixels(System.Windows.Media.Visual visual)
        {
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(210, 30, 96, 96,
                System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixels = new byte[210 * 30 * 4];
            bitmap.CopyPixels(pixels, 210 * 4, 0);
            return pixels;
        }
        Assert.Equal(Pixels(container), Pixels(preview));
        byte[] original = Pixels(preview);
        container.RenderTransform = new System.Windows.Media.TranslateTransform(73, 0);
        Assert.Equal(original, Pixels(preview));
        container.RenderTransform = null;
        label.Text = "Changed title";
        tab.UpdateLayout();
        Assert.Equal(Pixels(container), Pixels(preview));
        Assert.False(original.SequenceEqual(Pixels(preview)), "The preview must reflect the changed title.");
        Assert.Same(label, tab.Child);
        livePreview.Source = null;
        preview.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        Assert.Equal(new System.Windows.Size(), preview.DesiredSize);
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
