using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using KillerShell.Tests.Terminal;
using KillerShell.Tools;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class StorageContextKeyboardTests
{
    [Fact]
    public Task AppsOpensStorageContextWithoutAPointerAction()
        => RuntimeComponentProcess.Run(nameof(StorageContextKeyboardScenario));
}

public static class StorageContextKeyboardScenario
{
    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        var control = new StorageAnalyzerControl(System.IO.Path.GetTempPath());
        var window = new Window { Content = control, Width = 800, Height = 600,
            Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show();
            var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(control),
                Environment.TickCount, Key.Apps) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            control.RaiseEvent(input);
            Assert.True(input.Handled, "Apps must open the storage context menu.");
            var release = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(control),
                Environment.TickCount, Key.Apps) { RoutedEvent = Keyboard.PreviewKeyUpEvent };
            control.RaiseEvent(release);
            var menu = Assert.Single(PresentationSource.CurrentSources.Cast<PresentationSource>()
                .OfType<HwndSource>().Where(source => source.RootVisual != null)
                .SelectMany(source => Menus(source.RootVisual)), item => item.IsOpen);
            // The map card: with nothing selected it still offers the color modes as on/off tiles.
            var tiles = KillerShell.Controls.CardMenus.Buttons(menu).Select(b => b.CommandParameter).OfType<string>().ToArray();
            Assert.Contains("bytype", tiles);
            Assert.Contains("byfolder", tiles);
            menu.IsOpen = false;
        }
        finally { control.Shutdown(); window.Close(); app.Shutdown(); }
    }

    private static IEnumerable<ContextMenu> Menus(DependencyObject root)
    {
        if (root is ContextMenu menu) yield return menu;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Menus(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
