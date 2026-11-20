using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class ResultMenuRuntimeTests
{
    [Fact]
    public Task FolderFileAndBackgroundMenusOfferRelevantCommands()
        => RuntimeComponentProcess.Run(nameof(ResultMenuScenario));
}

public static class ResultMenuScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var pane = (FilePane)fixture.Window.FindName("LeftPane");
        var menu = pane.ResultsList.ContextMenu;
        MainWindow.DemoMode = true;
        try
        {
            const string parent = @"C:\Users\Demo\Documents";
            var folder = new SearchResult { FilePath = parent + @"\Runbooks", FileName = "Runbooks", Directory = parent, IsDirectory = true };
            var entry = DemoFs.Children(folder.FilePath).First(item => !item.IsDir);
            var file = new SearchResult { FilePath = Path.Combine(folder.FilePath, entry.Name), FileName = entry.Name, Directory = folder.FilePath };
            pane.Active.CurrentFolder = parent;
            pane.Active.Results.Clear();
            pane.Active.Results.Add(folder);
            pane.Active.Results.Add(file);
            pane.ResultsList.SelectedItem = folder;
            OpenMenu(pane, true);
            // A folder: the card names it, its tiles offer the verbs that apply to a folder (Edit and
            // Open with are dim), Delete sits in the header, and Copy as holds Path, Name and Folder
            // (no Lines or SHA-256).
            Assert.Equal("Runbooks", HeaderTitle(menu));
            foreach (string verb in new[] { "open", "cut", "copy", "rename", "properties" })
                Assert.True(Command(menu, verb).IsEnabled, verb + " is dim on a folder");
            Assert.False(Command(menu, "edit").IsEnabled);
            Assert.False(Command(menu, "openwith").IsEnabled);
            Assert.Single(KillerShell.Controls.CardMenus.Buttons(menu), b => (string?)b.Tag == KillerShell.Controls.CardMenus.HeaderDeleteTag);
            Assert.True(Offers(menu, "path") && Offers(menu, "name") && Offers(menu, "folder"));
            Assert.False(Offers(menu, "lines") || Offers(menu, "hash"));
            // A screen reader hears the whole command on a row, not just "Path".
            Assert.Equal(fixture.Window.FindResource("Str_Menu_CopyPath"), ((MenuItem)Command(menu, "path")).Header);
            // Clicking the card's header or the space around its tiles leaves it open.
            foreach (var slot in new[] { "cardhead", "cardtiles" })
                Assert.True(Tagged(menu.Items, slot).StaysOpenOnClick, slot + " closes the card on a click that does nothing");
            Assert.Contains("Runbooks.zip", ((MenuItem)Command(menu, "zip")).Header.ToString());
            Assert.Contains("Runbooks.tar.gz", ((MenuItem)Command(menu, "targz")).Header.ToString());
            // Every tile shows its shortcut.
            Assert.All(KillerShell.Controls.CardMenus.Commands(menu).OfType<Button>().Where(b => (string)b.CommandParameter is not ("copyas" or "archive")),
                b => Assert.False(string.IsNullOrEmpty(System.Windows.Automation.AutomationProperties.GetAcceleratorKey(b)), (string)b.CommandParameter + " has no shortcut"));

            pane.ResultsList.SelectedItem = file;
            OpenMenu(pane, true);
            Assert.Equal(entry.Name, HeaderTitle(menu));
            Assert.True(Command(menu, "edit").IsEnabled);
            Assert.True(Command(menu, "openwith").IsEnabled);
            Assert.True(Offers(menu, "hash"));

            // Empty space: the card names the folder being browsed, and only the folder verbs remain.
            OpenMenu(pane, false);
            Assert.Equal(pane.Active.Title, HeaderTitle(menu));
            Assert.False(Offers(menu, "open"));
            Assert.False(Offers(menu, "zip"));
            Assert.True(Command(menu, "paste").IsEnabled);
            Assert.True(Command(menu, "newfolder").IsEnabled);

            // Two rows: the card counts them.
            pane.ResultsList.SelectedItems.Add(folder);
            pane.ResultsList.SelectedItems.Add(file);
            OpenMenu(pane, true);
            Assert.Equal(string.Format((string)fixture.Window.FindResource("Str_Status_Selected"), 2), HeaderTitle(menu));

            pane.ResultsList.SelectedItems.Clear();
            pane.ResultsList.SelectedItem = folder;
            OpenMenu(pane, true);
            Command(menu, "targz").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            ShellWindowFixture.PumpUntil(() => DemoArchives.Contains(parent + @"\Runbooks.tar.gz"));
            Assert.NotEmpty(DemoArchives.List(parent + @"\Runbooks.tar.gz", "Runbooks"));
            ShellWindowFixture.PumpUntil(() => pane.Active.Results.Any(row => row.FileName == "Runbooks.tar.gz"));
            pane.ResultsList.SelectedItems.Clear();
            pane.ResultsList.SelectedItem = pane.Active.Results.First(row => row.FileName == "Runbooks.tar.gz");
            OpenMenu(pane, true);
            // The Open tile runs the same command Enter does: an archive opens as a folder.
            Command(menu, "open").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            ShellWindowFixture.PumpUntil(() => pane.Active.CurrentFolder == ArchiveProvider.Combine(parent + @"\Runbooks.tar.gz", ""));
        }
        finally { MainWindow.DemoMode = false; }
    }

    private static MenuItem Tagged(ItemCollection items, string tag)
        => Assert.Single(items.OfType<MenuItem>(), item => (string?)item.Tag == tag);

    private static FrameworkElement Command(ContextMenu menu, string command)
        => Assert.Single(KillerShell.Controls.CardMenus.Commands(menu),
            element => (element is Button button ? button.CommandParameter : element.Tag) as string == command);

    private static bool Offers(ContextMenu menu, string command)
        => KillerShell.Controls.CardMenus.Commands(menu)
            .Any(element => (element is Button button ? button.CommandParameter : element.Tag) as string == command);

    // The name UI Automation reads: the slot MenuItem's own, since a panel header has no peer.
    private static string HeaderTitle(ContextMenu menu)
        => System.Windows.Automation.AutomationProperties.GetName(Tagged(menu.Items, "cardhead"));

    private static void OpenMenu(FilePane pane, bool keyboard)
    {
        // WPF constructs these event arguments internally when input opens a context menu.
        var constructor = typeof(ContextMenuEventArgs).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(object), typeof(bool), typeof(double), typeof(double) }, null)!;
        var args = (ContextMenuEventArgs)constructor.Invoke(new object[] { pane.ResultsList, true, keyboard ? -1d : 0d, keyboard ? -1d : 0d });
        pane.ResultsList.RaiseEvent(args);
    }
}
