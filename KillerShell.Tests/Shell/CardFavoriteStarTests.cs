using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class CardFavoriteStarTests
{
    [Fact]
    public Task AFolderCardStarBookmarksTheFolderAndStaysOpen()
        => RuntimeComponentProcess.Run(nameof(CardFavoriteStarScenario));
}

public static class CardFavoriteStarScenario
{
    // A folder's card carries a star at the top right of its header: outline when the folder is
    // not saved, filled in the accent once it is. Toggling it saves or removes the folder and the
    // card stays open. A file's card has no star.
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        string folder = Path.Combine(fixture.Root, "Projects");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(fixture.Root, "notes.txt");
        File.WriteAllText(file, "x");
        var pane = window.Pane;

        var header = typeof(MainWindow).GetMethod("ResultCardHeader", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var fileHeader = (FrameworkElement)header.Invoke(window, [new List<SearchResult> { new() { FilePath = file, FileName = "notes.txt" } }])!;
        Assert.Empty(Descendants(fileHeader).OfType<ToggleButton>());

        var card = new ContextMenu();
        card.SetResourceReference(FrameworkElement.StyleProperty, "CardMenu");
        var slot = new MenuItem();
        slot.SetResourceReference(FrameworkElement.StyleProperty, "CardSlot");
        slot.Header = header.Invoke(window, [new List<SearchResult> { new() { FilePath = folder, FileName = "Projects", IsDirectory = true } }]);
        card.Items.Add(slot);
        card.PlacementTarget = pane.ResultsList;
        card.IsOpen = true;
        ShellWindowFixture.PumpUntil(() => card.IsOpen && card.IsVisible);

        var star = Assert.Single(Descendants((DependencyObject)slot.Header).OfType<ToggleButton>());
        Assert.Equal(Dock.Right, DockPanel.GetDock(star));
        Assert.Equal(VerticalAlignment.Top, star.VerticalAlignment);
        Assert.False(star.IsChecked);
        Assert.Equal(window.FindResource("Str_Menu_AddFavorite"), AutomationProperties.GetName(star));
        var toggle = (IToggleProvider)UIElementAutomationPeer.CreatePeerForElement(star).GetPattern(PatternInterface.Toggle);

        toggle.Toggle();
        ShellWindowFixture.PumpUntil(() => star.IsChecked == true);
        Assert.True(IsBookmarked(window, folder), "The star must save the folder.");
        Assert.True(card.IsOpen, "Toggling the star must leave the card open.");
        Assert.Equal(window.FindResource("Str_Menu_RemoveFavorite"), AutomationProperties.GetName(star));

        toggle.Toggle();
        ShellWindowFixture.PumpUntil(() => star.IsChecked == false);
        Assert.False(IsBookmarked(window, folder), "Unstarring must remove the folder.");
        Assert.True(card.IsOpen, "Untoggling the star must leave the card open.");
        card.IsOpen = false;
    }

    private static bool IsBookmarked(MainWindow window, string path)
        => (bool)typeof(MainWindow).GetMethod("IsBookmarked", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [path])!;

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        if (root is FrameworkElement fe) fe.ApplyTemplate();
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var deeper in Descendants(child)) yield return deeper;
        }
    }
}
