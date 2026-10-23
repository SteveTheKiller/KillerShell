using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerShell.Models;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
public sealed class AddressEditorTests
{
    [Fact]
    public Task SearchAddressRetainsItsPathDraftAndRowHeight()
        => RuntimeComponentProcess.Run(nameof(AddressEditorScenario));
}

public static class AddressEditorScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var left = (FilePane)window.FindName("LeftPane");
        var right = (FilePane)window.FindName("RightPane");
        window.ToggleDualPane();
        ShellWindowFixture.PumpUntil(() => right.Active.IsBrowsing);
        window.FocusPane(right);
        var search = new SearchTab("Search results") { CurrentFolder = "", RootPath = fixture.Root };
        left.Tabs.Add(search);
        left.Active = search;
        window.FocusPane(left);
        ShellWindowFixture.PumpUntil(() => left.ScopePathLabel.Text == fixture.Root && left.LocationRow.ActualHeight > 0);
        double rowHeight = left.LocationRow.ActualHeight;

        MouseUp(left.ScopePathLabel);
        ShellWindowFixture.PumpUntil(() => left.AddressBox.IsKeyboardFocusWithin);
        Assert.Equal(fixture.Root, left.AddressBox.Text);

        string draft = fixture.Root + "\\unfinished";
        left.AddressBox.Text = draft;
        left.AddressBox.Select(2, 0);
        MouseUp(left.AddressBox);
        ShellWindowFixture.PumpUntil(() => left.AddressBox.IsKeyboardFocusWithin);
        Assert.Equal(draft, left.AddressBox.Text);
        Assert.Equal(0, left.AddressBox.SelectionLength);
        window.UpdateLayout();
        Assert.Equal(rowHeight, left.LocationRow.ActualHeight, 2);

        left.ViewDetailsBtn.Focus();
        left.ViewDetailsBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ShellWindowFixture.PumpUntil(() => left.AddressBox.Visibility == Visibility.Collapsed);
        Assert.Equal(Visibility.Visible, left.ScopePathLabel.Visibility);
        Assert.Equal(fixture.Root, left.ScopePathLabel.Text);
        Assert.Equal("", search.CurrentFolder);
        Assert.Equal(fixture.Root, search.RootPath);
        Assert.Equal(rowHeight, left.LocationRow.ActualHeight, 2);

        MouseUp(left.ScopePathLabel);
        ShellWindowFixture.PumpUntil(() => left.AddressBox.IsKeyboardFocusWithin);
        window.FocusPane(right);
        Assert.True(right.ResultsList.Focus());
        ShellWindowFixture.PumpUntil(() => left.AddressBox.Visibility == Visibility.Collapsed);
        Assert.Equal(Visibility.Visible, left.ScopePathLabel.Visibility);
        Assert.Equal(Visibility.Visible, right.ScopePathLabel.Visibility);

        // Switching before the queued focus runs must not leave an unfocused editor open.
        window.FocusPane(left);
        MouseUp(left.ScopePathLabel);
        window.FocusPane(right);
        Assert.True(right.ResultsList.Focus());
        ShellWindowFixture.PumpUntil(() => left.AddressBox.Visibility == Visibility.Collapsed);
        Assert.Same(right, window.Pane);
        Assert.False(left.AddressBox.IsKeyboardFocusWithin);
        Assert.Equal(Visibility.Visible, left.ScopePathLabel.Visibility);

        MouseUp(right.ScopePathLabel);
        Assert.True(right.AddressBox.IsKeyboardFocusWithin);
        Assert.Equal(right.Active.CurrentFolder, right.AddressBox.Text);
        Assert.True(right.ResultsList.Focus());
        foreach (var theme in new[] { Theme.Dark, Theme.Sepulchre, Theme.SE98 })
        {
            window.FocusPane(left);
            ThemeManager.Apply(theme);
            window.UpdateLayout();
            double before = left.LocationRow.ActualHeight;
            MouseUp(left.ScopePathLabel);
            Assert.True(left.AddressBox.IsKeyboardFocusWithin);
            window.UpdateLayout();
            Assert.Equal(before, left.LocationRow.ActualHeight, 2);
            Assert.True(left.ResultsList.Focus());
            Assert.Equal(Visibility.Visible, left.ScopePathLabel.Visibility);
        }
    }

    private static void MouseUp(UIElement target)
        => target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = Mouse.MouseUpEvent });
}
