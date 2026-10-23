using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using KillerShell.Models;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
public sealed class SearchFlyoutLayoutTests
{
    [Fact]
    public Task SearchFormStartsCompactThenGrowsAndAdaptsToNarrowSpace()
        => RuntimeComponentProcess.Run(nameof(SearchFlyoutGrowthScenario));

    [Fact]
    public Task ScaledSearchKeepsTheEntireFormReachableInOneViewport()
        => RuntimeComponentProcess.Run(nameof(SearchFlyoutScrollScenario));

    [Fact]
    public Task SearchScopeCanUseResultsAndReturnToFolderSearch()
        => RuntimeComponentProcess.Run(nameof(SearchScopeResultsScenario));
}

public static class SearchFlyoutGrowthScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var view = new SearchFlyoutLayoutProbe(fixture.Window);
        view.SetRows(1, 0);
        view.Open();
        double compactHeight = view.Root.ActualHeight;
        Assert.InRange(compactHeight, 40, 340);
        view.AssertWholeFormViewport();
        Assert.True(view.SawOpeningFade, "Opening the search flyout should fade its surface into view.");
        var trigger = view.Named<Button>("SearchPanelBtn");
        view.AssertAnchoredBelowButton();
        Assert.Equal("\uE8BB", trigger.Content);
        Assert.DoesNotContain(SearchFlyoutLayoutProbe.Descendants<Button>(view.Root), button => button.Name == "TitleSearchToggle");
        Assert.Single(SearchFlyoutLayoutProbe.Descendants<TextBlock>(view.Root),
            label => label.Text == "KillerSearch");
        Assert.Equal("KillerSearch", view.Named<TextBlock>("SearchHeading").Text);
        Assert.True(view.Bounds(view.Named<TextBlock>("SearchHeading")).Bottom
            <= view.Bounds(view.Named<Button>("SearchScopeButton")).Top + 2);

        view.SetRows(4, 2);
        ShellWindowFixture.PumpUntil(() => view.Root.ActualHeight > compactHeight + 12);
        double rowsHeight = view.Root.ActualHeight;
        double rowsExtent = view.Viewport.ExtentHeight;
        var termsSection = view.Named<FrameworkElement>("SearchTermsSection");
        var filtersSection = view.Named<FrameworkElement>("SearchFiltersSection");
        Rect terms = view.Bounds(termsSection);
        Rect filters = view.Bounds(filtersSection);
        Assert.True(filters.Top >= terms.Bottom - 2,
            "Filters should follow terms vertically even in a wide window.");
        Assert.InRange(Math.Abs(terms.Left - filters.Left), 0, 1);
        Assert.InRange(Math.Abs(terms.Right - filters.Right), 0, 1);

        view.Advanced.Visibility = Visibility.Visible;
        ShellWindowFixture.PumpUntil(() => view.Viewport.ExtentHeight > rowsExtent + 12);
        Assert.True(view.Root.ActualHeight > rowsHeight + 12 || view.Viewport.ScrollableHeight > 0,
            "Opening advanced settings should grow the flyout or become reachable by scrolling at the height limit.");
        double formWidth = view.Named<FrameworkElement>("TitleSearchForm").ActualWidth;
        double surfaceWidth = view.Root.ActualWidth;
        var help = view.Named<FrameworkElement>("PatternHelpOverlay");
        ShellWindowFixture.Click(fixture.Window, "PatternHelpButton");
        Assert.True(view.Popup.StaysOpen, "Search should stay open until explicitly dismissed.");
        Assert.True(view.Popup.IsOpen);
        Assert.Equal(Visibility.Visible, help.Visibility);
        Assert.True(help.HasAnimatedProperties || help.RenderTransform.HasAnimatedProperties,
            "Pattern help should animate into the existing search flyout.");
        view.AssertSharedViewport(help);
        ShellWindowFixture.PumpUntil(() => help.Opacity >= 0.99 && help.ActualWidth > 0);
        var closeHelp = Assert.Single(SearchFlyoutLayoutProbe.Descendants<Button>(help));
        closeHelp.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ShellWindowFixture.PumpUntil(() => help.Visibility == Visibility.Collapsed);
        view.Refresh();
        Assert.True(view.Popup.IsOpen, "Closing pattern help should leave the search form open.");
        Assert.False(help.IsHitTestVisible, "Closed pattern help must not intercept the search form's input.");
        Assert.Equal(formWidth, view.Named<FrameworkElement>("TitleSearchForm").ActualWidth, 2);
        Assert.Equal(surfaceWidth, view.Root.ActualWidth, 2);
        fixture.Window.Width = 600;
        fixture.Window.ApplyAppScale(1.5);
        view.Refresh();
        ShellWindowFixture.PumpUntil(() => view.Bounds(filtersSection).Top >= view.Bounds(termsSection).Bottom - 2);
        terms = view.Bounds(termsSection);
        filters = view.Bounds(filtersSection);
        Assert.True(terms.Left >= -2 && terms.Right <= view.Viewport.ActualWidth + 2);
        Assert.True(filters.Left >= -2 && filters.Right <= view.Viewport.ActualWidth + 2);
        view.AssertWithinWorkArea();
        view.Close();
    }
}

public static class SearchScopeResultsScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var view = new SearchFlyoutLayoutProbe(fixture.Window);
        var pane = (FilePane)fixture.Window.FindName("LeftPane");
        pane.Active.IsBrowsing = false;
        pane.Active.Results.Clear();
        pane.Active.Results.Add(new SearchResult { FilePath = @"C:\Users\Demo\Documents\sample.txt", FileName = "sample.txt" });
        pane.Active.Results.Add(new SearchResult { FilePath = @"C:\Users\Demo\Documents\second.txt", FileName = "second.txt" });
        view.Open();
        var scope = view.Named<Button>("SearchScopeButton");
        scope.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ShellWindowFixture.PumpUntil(() => scope.ContextMenu?.IsOpen == true);
        var within = Assert.Single(scope.ContextMenu.Items.OfType<MenuItem>(),
            item => item.InputGestureText == "Ctrl+Shift+F");
        Assert.False(within.IsChecked);
        scope.ContextMenu.IsOpen = false;
        within.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        ShellWindowFixture.PumpUntil(() => pane.Active.PipeFiles != null);
        Assert.Equal(new[] { @"C:\Users\Demo\Documents\sample.txt", @"C:\Users\Demo\Documents\second.txt" },
            pane.Active.PipeFiles!.OrderBy(path => path, StringComparer.Ordinal));
        Assert.Equal(within.Header, view.Named<TextBlock>("SearchScopeLabel").Text);
        var firstRefinement = pane.Active;
        firstRefinement.Results.Add(new SearchResult { FilePath = @"C:\Users\Demo\Documents\second.txt", FileName = "second.txt" });
        scope.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ShellWindowFixture.PumpUntil(() => scope.ContextMenu.IsOpen);
        within = Assert.Single(scope.ContextMenu.Items.OfType<MenuItem>(), item => item.IsChecked);
        scope.ContextMenu.IsOpen = false;
        within.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        ShellWindowFixture.PumpUntil(() => !ReferenceEquals(firstRefinement, pane.Active));
        Assert.Equal(new[] { @"C:\Users\Demo\Documents\second.txt" }, pane.Active.PipeFiles);
        scope.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ShellWindowFixture.PumpUntil(() => scope.ContextMenu.IsOpen);
        Assert.Single(scope.ContextMenu.Items.OfType<MenuItem>(), item => item.IsChecked);
        var windowScope = scope.ContextMenu.Items.OfType<MenuItem>().First();
        scope.ContextMenu.IsOpen = false;
        windowScope.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Assert.Null(pane.Active.PipeFiles);
        Assert.Equal(windowScope.Header, view.Named<TextBlock>("SearchScopeLabel").Text);
        Assert.Equal(pane.Active.RootPath, pane.ScopePathLabel.Text);
        view.Close();
    }
}

public static class SearchFlyoutScrollScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        fixture.Window.Width = 600;
        fixture.Window.Height = 520;
        var view = new SearchFlyoutLayoutProbe(fixture.Window);
        view.SetRows(12, 10);
        view.Advanced.Visibility = Visibility.Visible;
        foreach (double scale in new[] { 1.5, 2.5 })
        {
            fixture.Window.ApplyAppScale(scale);
            view.Open();
            view.AssertWholeFormViewport();
            ShellWindowFixture.PumpUntil(() => view.Viewport.ScrollableHeight > 0);
            Assert.Equal(scale, view.Root.LayoutTransform.Value.M11, 2);
            Assert.Equal(scale, view.Root.LayoutTransform.Value.M22, 2);
            view.AssertWithinWorkArea();
            view.AssertAnchoredBelowButton();

            foreach (string name in new[] { "SearchScopeButton", "SearchButton", "IncludePatternsBox", "ExcludePatternsBox", "CaseSensitiveCheck" })
            {
                var control = view.Named<FrameworkElement>(name);
                control.BringIntoView();
                view.Refresh();
                ShellWindowFixture.PumpUntil(() => view.IsInsideViewport(control), diagnostic: () =>
                    name + " bounds=" + view.Bounds(control) + "; viewport=" + view.Viewport.RenderSize);
            }
            view.Viewport.ScrollToEnd();
            view.Refresh();
            ShellWindowFixture.PumpUntil(() => Math.Abs(view.Viewport.VerticalOffset - view.Viewport.ScrollableHeight) < 1);
            view.Close();
        }
    }
}

internal sealed class SearchFlyoutLayoutProbe
{
    private readonly MainWindow _window;
    private readonly SearchTab _tab;
    internal Popup Popup { get; }
    internal ScrollViewer Viewport { get; }
    internal ItemsControl Terms { get; }
    internal ItemsControl Filters { get; }
    internal FrameworkElement Advanced { get; }
    internal FrameworkElement Root => (FrameworkElement)Popup.Child;
    internal bool SawOpeningFade { get; private set; }

    internal SearchFlyoutLayoutProbe(MainWindow window)
    {
        _window = window;
        _tab = ((FilePane)window.FindName("LeftPane")).Active;
        Popup = Named<Popup>("TitleSearchPopup");
        Viewport = Named<ScrollViewer>("TitleSearchViewport");
        Terms = Named<ItemsControl>("TermsList");
        Filters = Named<ItemsControl>("FiltersList");
        Advanced = Named<FrameworkElement>("AdvancedPanel");
        Advanced.Visibility = Visibility.Collapsed;
        Popup.Opened += (_, _) => SawOpeningFade = Popup.PopupAnimation == PopupAnimation.Fade || Root.Opacity < 0.99;
    }

    internal T Named<T>(string name) where T : class
        => Assert.IsAssignableFrom<T>(_window.FindName(name));

    internal void SetRows(int terms, int filters)
    {
        _tab.Groups.Clear();
        var group = new TermGroup();
        for (int i = 0; i < terms; i++) group.Terms.Add(new SearchTerm { Pattern = "sample" + i });
        _tab.Groups.Add(group);
        _tab.Filters.Clear();
        for (int i = 0; i < filters; i++) _tab.Filters.Add(new SearchFilter { Text = "txt" });
        Refresh();
    }

    internal void Open()
    {
        if (!Popup.IsOpen) ShellWindowFixture.Click(_window, "SearchPanelBtn");
        Refresh();
        ShellWindowFixture.PumpUntil(() => Popup.IsOpen && Root.ActualHeight > 0 && Viewport.ActualHeight > 0 && Root.Opacity >= 0.99,
            diagnostic: () => "Popup open=" + Popup.IsOpen + "; opacity=" + Root.Opacity
                + "; surface=" + Root.RenderSize + "; viewport=" + Viewport.RenderSize
                + "; scale=" + Root.LayoutTransform.Value.M11 + "; window=" + _window.RenderSize);
        Assert.Same(Terms, _window.FindName("TermsList"));
        Assert.Same(Filters, _window.FindName("FiltersList"));
    }

    internal void Refresh()
    {
        _window.UpdateLayout();
        Root.UpdateLayout();
    }

    internal void Close()
    {
        Popup.IsOpen = false;
        ShellWindowFixture.PumpUntil(() => !Popup.IsOpen && Root.Opacity == 0
            && Named<Button>("SearchPanelBtn").Tag == null);
    }

    internal void AssertWholeFormViewport()
    {
        foreach (var control in new FrameworkElement[]
            { Named<Button>("SearchScopeButton"), Terms, Filters, Named<Button>("SearchButton"), Advanced })
            AssertSharedViewport(control);
    }

    internal void AssertSharedViewport(FrameworkElement control)
        => Assert.True(ReferenceEquals(Viewport, ScrollParent(control)),
            control.Name + " must belong directly to the shared search viewport, without an intervening scroll viewer.");

    internal Rect Bounds(FrameworkElement element)
        => element.TransformToAncestor(Viewport).TransformBounds(new Rect(element.RenderSize));

    internal bool IsInsideViewport(FrameworkElement element)
    {
        Rect bounds = Bounds(element);
        return bounds.Top >= -2 && bounds.Bottom <= Viewport.ActualHeight + 2
            && bounds.Left >= -2 && bounds.Right <= Viewport.ActualWidth + 2;
    }

    internal void AssertWithinWorkArea()
    {
        Rect scaled = Root.LayoutTransform.TransformBounds(new Rect(Root.RenderSize));
        Assert.True(scaled.Height <= SystemParameters.WorkArea.Height + 2, "The flyout must fit the work area's height.");
        Assert.True(scaled.Width <= SystemParameters.WorkArea.Width + 2, "The flyout must fit the work area's width.");
    }

    internal void AssertAnchoredBelowButton()
    {
        var trigger = Named<Button>("SearchPanelBtn");
        Point buttonEdge = trigger.PointToScreen(new Point(trigger.ActualWidth, trigger.ActualHeight));
        Point flyoutEdge = Root.PointToScreen(new Point(Root.ActualWidth, 0));
        Assert.True(Math.Abs(buttonEdge.X - flyoutEdge.X) <= 4,
            "Flyout right edge=" + flyoutEdge + "; button edge=" + buttonEdge
            + "; scale=" + Root.LayoutTransform.Value.M11 + "; surface=" + Root.RenderSize);
        Assert.InRange(flyoutEdge.Y - buttonEdge.Y, 2, 8);
    }

    private static ScrollViewer? ScrollParent(DependencyObject child)
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent != null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer viewer) return viewer;
        return null;
    }

    internal static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
