using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KillerShell.Models;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class DetailsLayoutTests
{
    [Fact]
    public Task RightPreviewReservesTheHeaderAndKeepsListingScrollbarsReachable()
        => RuntimeComponentProcess.Run(nameof(DetailsLayoutScenario));
}

public static class DetailsLayoutScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        window.Width = 800;
        window.Height = 520;
        var pane = (FilePane)window.FindName("LeftPane");
        pane.Active.Results.Clear();
        for (int i = 0; i < 160; i++)
            pane.Active.Results.Add(new SearchResult
            {
                FileName = "document-" + i + ".txt", FilePath = fixture.Root + "\\document-" + i + ".txt",
                Directory = fixture.Root, SizeBytes = 2048, Modified = DateTime.Today, Seq = i
            });
        pane.ViewDetailsBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        pane.DetailsPaneBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        pane.DetailsPaneBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ShellWindowFixture.PumpUntil(() => pane.DetailsPane.IsVisible && pane.ResultsList.ActualWidth > 0);
        window.UpdateLayout();
        var header = pane.DetailsHeader.TransformToAncestor(pane).TransformBounds(new Rect(pane.DetailsHeader.RenderSize));
        var preview = pane.DetailsPane.TransformToAncestor(pane).TransformBounds(new Rect(pane.DetailsPane.RenderSize));
        Assert.True(header.Right <= preview.Left + 1, "The table header must end before the right preview.");

        double columns = pane.ViewState.RowIconColumn.Value + pane.ViewState.NameWidth.Value
            + pane.ViewState.LocationWidth.Value + pane.ViewState.SizeWidth.Value
            + pane.ViewState.ModifiedWidth.Value + pane.ViewState.TypeWidth.Value + pane.ViewState.CreatedWidth.Value;
        Assert.True(columns + pane.ViewState.RowPad.Left + pane.ViewState.RowPad.Right <= pane.ResultsList.ActualWidth + 1,
            "Details columns must fit the remaining listing width.");
        var scroll = FindScroll(pane.ResultsList)!;
        ShellWindowFixture.PumpUntil(() => scroll.ScrollableHeight > 0);
        Assert.Equal(Visibility.Visible, scroll.ComputedVerticalScrollBarVisibility);
        scroll.ScrollToEnd();
        ShellWindowFixture.PumpUntil(() => Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1);

        pane.ViewListBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ShellWindowFixture.PumpUntil(() => scroll.ScrollableWidth > 0);
        Assert.Equal(Visibility.Visible, scroll.ComputedHorizontalScrollBarVisibility);
        scroll.ScrollToRightEnd();
        ShellWindowFixture.PumpUntil(() => Math.Abs(scroll.HorizontalOffset - scroll.ScrollableWidth) < 1);
    }

    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScroll(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }
}
