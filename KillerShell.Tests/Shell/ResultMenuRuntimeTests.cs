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
            Assert.Equal(fixture.Window.FindResource("Str_Btn_Open"), Gesture(menu, "Enter").Header);
            var special = Tagged(menu.Items, "copyspecial");
            Assert.Equal(5, special.Items.Count);
            Assert.Equal(2, special.Items.OfType<MenuItem>().Count(item => item.Visibility == Visibility.Visible));
            Assert.Equal(Visibility.Visible, Gesture(menu, "Ctrl+C").Visibility);
            Assert.Equal(Visibility.Collapsed, Gesture(menu, "Ctrl+V").Visibility);
            Assert.Equal(Visibility.Collapsed, Gesture(menu, "Ctrl+Shift+N").Visibility);
            var archives = Tagged(menu.Items, "archiveoptions");
            Assert.Equal(Visibility.Visible, archives.Visibility);
            Assert.Contains("Runbooks.zip", Tagged(archives.Items, "zip").Header.ToString());
            Assert.Contains("Runbooks.tar.gz", Tagged(archives.Items, "targz").Header.ToString());
            Assert.Contains(menu.Items.OfType<MenuItem>(), item => item.InputGestureText == "Shift+F4");

            pane.ResultsList.SelectedItem = file;
            OpenMenu(pane, true);
            Assert.Equal(fixture.Window.FindResource("Str_Menu_OpenFile"), Gesture(menu, "Enter").Header);
            Assert.Equal(4, special.Items.OfType<MenuItem>().Count(item => item.Visibility == Visibility.Visible));
            Assert.Equal(Visibility.Collapsed, Tagged(special.Items, "matchedlines").Visibility);
            OpenMenu(pane, false);
            Assert.Equal(Visibility.Collapsed, special.Visibility);
            Assert.Equal(Visibility.Collapsed, archives.Visibility);
            Assert.Equal(Visibility.Collapsed, Gesture(menu, "Ctrl+C").Visibility);
            Assert.Equal(Visibility.Visible, Gesture(menu, "Ctrl+V").Visibility);
            Assert.Equal(Visibility.Visible, Gesture(menu, "Ctrl+Shift+N").Visibility);

            pane.ResultsList.SelectedItem = folder;
            OpenMenu(pane, true);
            Tagged(archives.Items, "targz").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            ShellWindowFixture.PumpUntil(() => DemoArchives.Contains(parent + @"\Runbooks.tar.gz"));
            Assert.NotEmpty(DemoArchives.List(parent + @"\Runbooks.tar.gz", "Runbooks"));
            ShellWindowFixture.PumpUntil(() => pane.Active.Results.Any(row => row.FileName == "Runbooks.tar.gz"));
            pane.ResultsList.SelectedItem = pane.Active.Results.First(row => row.FileName == "Runbooks.tar.gz");
            OpenMenu(pane, true);
            Gesture(menu, "Enter").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            ShellWindowFixture.PumpUntil(() => pane.Active.CurrentFolder == ArchiveProvider.Combine(parent + @"\Runbooks.tar.gz", ""));
        }
        finally { MainWindow.DemoMode = false; }
    }

    private static MenuItem Tagged(ItemCollection items, string tag)
        => Assert.Single(items.OfType<MenuItem>(), item => (string?)item.Tag == tag);

    private static MenuItem Gesture(ContextMenu menu, string gesture)
        => Assert.Single(menu.Items.OfType<MenuItem>(), item => item.InputGestureText == gesture);

    private static void OpenMenu(FilePane pane, bool keyboard)
    {
        // WPF constructs these event arguments internally when input opens a context menu.
        var constructor = typeof(ContextMenuEventArgs).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(object), typeof(bool), typeof(double), typeof(double) }, null)!;
        var args = (ContextMenuEventArgs)constructor.Invoke(new object[] { pane.ResultsList, true, keyboard ? -1d : 0d, keyboard ? -1d : 0d });
        pane.ResultsList.RaiseEvent(args);
    }
}
