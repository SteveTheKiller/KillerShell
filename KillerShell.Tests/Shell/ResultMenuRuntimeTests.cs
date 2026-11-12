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
            // A folder: the card names it, offers the five verbs that apply to a folder (no Edit),
            // and the Path and Folder chips (no Name, Lines or SHA-256).
            Assert.Equal("Runbooks", HeaderTitle(menu));
            var quick = Tagged(menu.Items, "cardquick");
            var chips = Tagged(menu.Items, "cardcopy");
            Assert.Equal(Visibility.Visible, quick.Visibility);
            Assert.Equal(new[] { "open", "cut", "copy", "rename", "delete" }, Shown(quick));
            Assert.Equal(new[] { "path", "folder" }, Shown(chips));
            Assert.Equal(Visibility.Collapsed, Gesture(menu, "Ctrl+V").Visibility);
            Assert.Equal(Visibility.Collapsed, Gesture(menu, "Ctrl+Shift+N").Visibility);
            Assert.Equal(Visibility.Collapsed, Gesture(menu, "Ctrl+Shift+O").Visibility);
            var archives = Tagged(menu.Items, "archiveoptions");
            Assert.Equal(Visibility.Visible, archives.Visibility);
            Assert.Contains("Runbooks.zip", Tagged(archives.Items, "zip").Header.ToString());
            Assert.Contains("Runbooks.tar.gz", Tagged(archives.Items, "targz").Header.ToString());
            Assert.Contains(Tagged(menu.Items, "folderoptions").Items.OfType<MenuItem>(), item => item.InputGestureText == "Shift+F4");

            pane.ResultsList.SelectedItem = file;
            OpenMenu(pane, true);
            Assert.Equal(entry.Name, HeaderTitle(menu));
            Assert.Equal(new[] { "open", "edit", "cut", "copy", "rename", "delete" }, Shown(quick));
            Assert.Equal(new[] { "path", "name", "folder", "hash" }, Shown(chips));
            Assert.Equal(Visibility.Visible, Gesture(menu, "Ctrl+Shift+O").Visibility);

            // Empty space: the card names the folder being browsed, and only the folder verbs remain.
            OpenMenu(pane, false);
            Assert.Equal(pane.Active.Title, HeaderTitle(menu));
            Assert.Equal(Visibility.Collapsed, quick.Visibility);
            Assert.Equal(Visibility.Collapsed, chips.Visibility);
            Assert.Equal(Visibility.Collapsed, archives.Visibility);
            Assert.Equal(Visibility.Visible, Gesture(menu, "Ctrl+V").Visibility);
            Assert.Equal(Visibility.Visible, Gesture(menu, "Ctrl+Shift+N").Visibility);

            // Two rows: the card counts them.
            pane.ResultsList.SelectedItems.Add(folder);
            pane.ResultsList.SelectedItems.Add(file);
            OpenMenu(pane, true);
            Assert.Equal(string.Format((string)fixture.Window.FindResource("Str_Status_Selected"), 2), HeaderTitle(menu));

            pane.ResultsList.SelectedItems.Clear();
            pane.ResultsList.SelectedItem = folder;
            OpenMenu(pane, true);
            Tagged(archives.Items, "targz").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            ShellWindowFixture.PumpUntil(() => DemoArchives.Contains(parent + @"\Runbooks.tar.gz"));
            Assert.NotEmpty(DemoArchives.List(parent + @"\Runbooks.tar.gz", "Runbooks"));
            ShellWindowFixture.PumpUntil(() => pane.Active.Results.Any(row => row.FileName == "Runbooks.tar.gz"));
            pane.ResultsList.SelectedItem = pane.Active.Results.First(row => row.FileName == "Runbooks.tar.gz");
            OpenMenu(pane, true);
            // The quick Open button runs the same command the Open row did: an archive opens as a folder.
            QuickButton(quick, "open").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            ShellWindowFixture.PumpUntil(() => pane.Active.CurrentFolder == ArchiveProvider.Combine(parent + @"\Runbooks.tar.gz", ""));
        }
        finally { MainWindow.DemoMode = false; }
    }

    private static MenuItem Tagged(ItemCollection items, string tag)
        => Assert.Single(items.OfType<MenuItem>(), item => (string?)item.Tag == tag);

    private static MenuItem Gesture(ContextMenu menu, string gesture)
        => Assert.Single(menu.Items.OfType<MenuItem>(), item => item.InputGestureText == gesture);

    private static Button[] Buttons(MenuItem slot)
    {
        var found = new System.Collections.Generic.List<Button>();
        void Walk(object node)
        {
            if (node is Button button) { found.Add(button); return; }
            if (node is DependencyObject d) foreach (var child in LogicalTreeHelper.GetChildren(d)) Walk(child);
        }
        Walk(slot.Header);
        return found.ToArray();
    }

    private static string[] Shown(MenuItem slot)
        => Buttons(slot).Where(b => b.Visibility == Visibility.Visible).Select(b => (string)b.CommandParameter).ToArray();

    private static Button QuickButton(MenuItem slot, string command)
        => Assert.Single(Buttons(slot), b => (string)b.CommandParameter == command);

    private static string HeaderTitle(ContextMenu menu)
    {
        var header = Assert.IsAssignableFrom<FrameworkElement>(Tagged(menu.Items, "cardhead").Header);
        return System.Windows.Automation.AutomationProperties.GetName(header);
    }

    private static void OpenMenu(FilePane pane, bool keyboard)
    {
        // WPF constructs these event arguments internally when input opens a context menu.
        var constructor = typeof(ContextMenuEventArgs).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(object), typeof(bool), typeof(double), typeof(double) }, null)!;
        var args = (ContextMenuEventArgs)constructor.Invoke(new object[] { pane.ResultsList, true, keyboard ? -1d : 0d, keyboard ? -1d : 0d });
        pane.ResultsList.RaiseEvent(args);
    }
}
