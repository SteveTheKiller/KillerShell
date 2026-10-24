using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using KillerShell.Services;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class ShellAccessibilityNamesTests
{
    [Fact]
    public Task NativeTreeBookmarksAndHelpCloseExposeLiveNames()
        => RuntimeComponentProcess.Run(nameof(ShellAccessibilityNamesScenario));
}

public static class ShellAccessibilityNamesScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var tree = (TreeView)window.FindName("FolderTree");
        ShellWindowFixture.Click(window, "BookmarksBtn");
        ShellWindowFixture.PumpUntil(() => tree.IsVisible && tree.ActualWidth > 100);
        var roots = (ObservableCollection<FolderNode>)tree.ItemsSource;
        var group = new FolderNode("", "Fixture group", false, isGroup: true);
        var node = new FolderNode(fixture.Root, "Fixture folder", false);
        group.Children.Add(node);
        roots.Clear();
        roots.Add(group);
        group.IsExpanded = true;
        var bookmarks = (ListBox)window.FindName("BookmarksList");
        var places = (ObservableCollection<Bookmark>)bookmarks.ItemsSource;
        var bookmark = new Bookmark { Path = fixture.Root };
        places.Clear();
        places.Add(bookmark);
        window.UpdateLayout();
        var rootItem = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(group);
        rootItem.IsExpanded = true;
        ShellWindowFixture.PumpUntil(() => rootItem.ItemContainerGenerator.ContainerFromItem(node) is TreeViewItem);
        ShellWindowFixture.PumpUntil(() => bookmarks.ItemContainerGenerator.ContainerFromItem(bookmark) is ListBoxItem);
        var treeItem = (TreeViewItem)rootItem.ItemContainerGenerator.ContainerFromItem(node);
        var bookmarkItem = (ListBoxItem)bookmarks.ItemContainerGenerator.ContainerFromItem(bookmark);
        AutomationProperties.SetAutomationId(treeItem, "fixture-folder");
        AutomationProperties.SetAutomationId(bookmarkItem, "fixture-bookmark");
        treeItem.IsSelected = true;
        IntPtr handle = new WindowInteropHelper(window).Handle;
        Assert.Equal(node.NameLabel, NativeName(handle, "fixture-folder"));
        Assert.Equal(bookmark.NameLabel, NativeName(handle, "fixture-bookmark"));
        Nicknames.Set(fixture.Root, "Current nickname");
        Assert.Equal(node.NameLabel, NativeName(handle, "fixture-folder"));
        Assert.Equal(bookmark.NameLabel, NativeName(handle, "fixture-bookmark"));
        Assert.True(treeItem.IsSelected);
        Assert.True(rootItem.IsExpanded);

        var help = Descendants(window).OfType<Button>().Single(b => Equals(b.Content, "?"));
        help.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var overlay = (Grid)window.FindName("ShortcutsOverlay");
        ShellWindowFixture.PumpUntil(() => overlay.Visibility == Visibility.Visible);
        var close = Descendants(overlay).OfType<Button>().Single(b => ReferenceEquals(b.Style, window.FindResource("OverlayCloseButton")));
        AutomationProperties.SetAutomationId(close, "fixture-help-close");
        Assert.Equal((string)window.FindResource("Str_Menu_Close"), NativeName(handle, "fixture-help-close", TreeScope.Children));
        LocaleManager.Apply(Locale.Fr);
        Assert.Equal((string)window.FindResource("Str_Menu_Close"), NativeName(handle, "fixture-help-close", TreeScope.Children));
        Assert.Equal(close.ToolTip, window.FindResource("Str_Menu_Close"));
        close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ShellWindowFixture.PumpUntil(() => overlay.Visibility == Visibility.Collapsed);
    }

    private static string NativeName(IntPtr handle, string id, TreeScope scope = TreeScope.Descendants)
    {
        Console.WriteLine("query " + id);
        var task = Task.Factory.StartNew(() =>
        {
            Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
            var element = AutomationElement.FromHandle(handle).FindFirst(scope,
                new PropertyCondition(AutomationElement.AutomationIdProperty, id));
            Assert.NotNull(element);
            string name = element.Current.Name;
            Console.WriteLine(id + ": " + name);
            return name;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var frame = new System.Windows.Threading.DispatcherFrame();
        var deadline = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        deadline.Tick += (_, _) => frame.Continue = false;
        task.ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)), TaskScheduler.Default);
        deadline.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        deadline.Stop();
        Assert.True(task.IsCompleted, "Native UIA name query did not finish: " + id);
        return task.GetAwaiter().GetResult();
    }

    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
