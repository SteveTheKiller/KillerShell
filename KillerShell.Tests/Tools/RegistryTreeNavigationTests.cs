using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerShell.Shell;
using KillerShell.Tools;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
public sealed class RegistryTreeNavigationTests
{
    [Fact]
    public Task LongKeySelectionKeepsTheLeftEdgeAndRevealsItsRow()
        => RuntimeComponentProcess.Run(nameof(RegistryTreeNavigationScenario));
}

public static class RegistryTreeNavigationScenario
{
    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        bool originalDemo = MainWindow.DemoMode;
        MainWindow.DemoMode = true;
        var control = new RegistryEditorControl();
        var window = new Window
        {
            Content = control, Width = 700, Height = 360,
            Opacity = 0, ShowInTaskbar = false, ShowActivated = false
        };
        try
        {
            var tree = Descendants(control).OfType<TreeView>().Single();
            var root = new RegistryNode("HKEY_USERS", "HKEY_USERS", isRoot: true);
            root.LoadChildren();
            root.Children.Clear();
            for (int index = 0; index < 80; index++)
            {
                string name = "S-1-5-21-1234567890-1234567890-1234567890-" + index.ToString("D4");
                root.Children.Add(new RegistryNode(root.FullPath + "\\" + name, name, isPlaceholder: true)
                    { Parent = root });
            }
            tree.ItemsSource = new[] { root };
            root.IsExpanded = true;
            window.Show();
            window.UpdateLayout();
            var scroll = Descendants(tree).OfType<ScrollViewer>().First();
            var selected = root.Children.Last();
            var item = Descendants(tree).OfType<TreeViewItem>()
                .Single(candidate => ReferenceEquals(candidate.DataContext, selected));
            item.IsSelected = true;
            item.Focus();
            var header = (FrameworkElement)item.Template.FindName("PART_Header", item);
            header.BringIntoView();
            ShellWindowFixture.PumpUntil(() => scroll.VerticalOffset > 0);
            Assert.Equal(0, scroll.HorizontalOffset);
            Assert.Same(selected, tree.SelectedItem);
            double top = header.TranslatePoint(new Point(), scroll).Y;
            Assert.InRange(top, 0, scroll.ActualHeight - header.ActualHeight);

            var label = Descendants(header).OfType<TextBlock>().Single();
            Assert.Equal(selected.FullPath, label.ToolTip);
            Assert.Equal(TextTrimming.CharacterEllipsis, label.TextTrimming);
            Assert.True(label.TranslatePoint(new Point(label.ActualWidth, 0), scroll).X <= scroll.ViewportWidth);
            Assert.False(scroll.CanContentScroll);
            Assert.NotNull(scroll.Template.FindName("ViewportFadeHost", scroll));

            var rootItem = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromItem(root);
            rootItem.IsSelected = true;
            rootItem.Focus();
            ((FrameworkElement)rootItem.Template.FindName("PART_Header", rootItem)).BringIntoView();
            ShellWindowFixture.PumpUntil(() => scroll.VerticalOffset == 0);
            Assert.Equal(0, scroll.HorizontalOffset);
            Assert.Same(root, tree.SelectedItem);
        }
        finally
        {
            control.Shutdown();
            window.Close();
            MainWindow.DemoMode = originalDemo;
            app.Shutdown();
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
