using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using KillerShell.Editing;
using KillerShell.Models;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
public sealed class TitlebarContextTests
{
    [Fact]
    public Task ToolbarAddressKeepsEditingWhileTitleAndSearchDoNotResizePanes()
        => RuntimeComponentProcess.Run(nameof(TitlebarContextScenario));
}

public static class TitlebarContextScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var left = (FilePane)window.FindName("LeftPane");
        var right = (FilePane)window.FindName("RightPane");
        var title = (ContentControl)window.FindName("TitleContextHost");
        Assert.Same(left.LocationRow, left.ScopeBar.Parent);
        Assert.False(title.IsHitTestVisible);
        Assert.Equal(left.Active.Title, ((TextBlock)title.Content).Text);
        Assert.Null(window.FindName("DensityBtn"));
        Assert.NotNull(left.FileDensityBtn);

        string destination = Path.Combine(fixture.Root, "address-target");
        Directory.CreateDirectory(destination);
        PaneKeyboardScenario.Raise(left.ResultsList, Key.L, ModifierKeys.Control);
        ShellWindowFixture.PumpUntil(() => left.AddressBox.IsKeyboardFocusWithin);
        left.AddressBox.Text = destination;
        left.AddressBox.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(left.AddressBox), 0, Key.Enter)
            { RoutedEvent = Keyboard.KeyDownEvent });
        ShellWindowFixture.PumpUntil(() => left.Active.CurrentFolder == destination);
        Assert.Equal(destination, left.ScopePathLabel.Text);
        left.RecentsBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ShellWindowFixture.PumpUntil(() => left.RecentsMenu.IsOpen);
        left.RecentsMenu.IsOpen = false;

        ShellWindowFixture.Click(window, "DualPaneRailBtn");
        ShellWindowFixture.PumpUntil(() => right.IsVisible && right.Active != null);
        window.FocusPane(right);
        Assert.Same(right.LocationRow, right.ScopeBar.Parent);
        Assert.Equal(Visibility.Visible, left.ScopeBar.Visibility);
        Assert.Equal(right.Active.Title, ((TextBlock)title.Content).Text);

        var terminal = new SearchTab("Terminal") { Term = new TerminalControl(TerminalSkin.Default) };
        var editor = new SearchTab("Editor") { Editor = new EditorControl("") };
        foreach (var tab in new[] { terminal, editor })
        {
            window.FocusPane(left);
            right.Tabs.Add(tab);
            right.Active = tab;
            window.FocusPane(right);
            Assert.Equal(tab.Title, ((TextBlock)title.Content).Text);
            Assert.NotNull(right.TermPathHost.Parent);
            Assert.NotNull(right.EditorPathHost.Parent);
            int before = right.ViewState.Density;
            (tab.Term != null ? right.TermDensityBtn : right.EditorDensityBtn).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal((before + 1) % 5, right.ViewState.Density);
        }

        double paneWidth = right.ActualWidth;
        var search = (Button)window.FindName("SearchPanelBtn");
        search.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var popup = (Popup)window.FindName("TitleSearchPopup");
        ShellWindowFixture.PumpUntil(() => popup.IsOpen);
        window.UpdateLayout();
        Assert.Equal(paneWidth, right.ActualWidth, 2);
        Assert.Equal(0, ((ColumnDefinition)window.FindName("SearchCol")).ActualWidth);
        var popupGrid = Assert.IsType<Grid>(popup.Child);
        var viewport = (ScrollViewer)window.FindName("TitleSearchViewport");
        window.Height = 520;
        foreach (double scale in new[] { 1.5, 2.5 })
        {
            window.ApplyAppScale(scale);
            window.UpdateLayout();
            popupGrid.UpdateLayout();
            ShellWindowFixture.PumpUntil(() => popupGrid.LayoutTransform.Value.M11 == scale && viewport.ActualHeight > 0);
            Assert.Equal(scale, popupGrid.LayoutTransform.Value.M22, 2);
            Assert.Equal(TextFormattingMode.Ideal, TextOptions.GetTextFormattingMode(popupGrid));
            Assert.Equal(TextRenderingMode.Grayscale, TextOptions.GetTextRenderingMode(popupGrid));
            Assert.True(popupGrid.LayoutTransform.TransformBounds(new Rect(popupGrid.RenderSize)).Height <= window.ActualHeight,
                "The scaled search form must fit the available height.");
            Assert.True(viewport.ExtentHeight <= viewport.ViewportHeight + viewport.ScrollableHeight + 1,
                "The complete form must fit or remain reachable by scrolling in a short window.");
            viewport.ScrollToEnd();
            viewport.UpdateLayout();
            Assert.Equal(viewport.ScrollableHeight, viewport.VerticalOffset, 2);
        }
        popup.IsOpen = false;
        window.ApplyAppScale(1);
        search.ContextMenu.IsOpen = true;
        Assert.Equal(3, search.ContextMenu.Items.Count);
        foreach (MenuItem item in search.ContextMenu.Items) Assert.NotNull(item.Icon);
        search.ContextMenu.IsOpen = false;
    }
}
