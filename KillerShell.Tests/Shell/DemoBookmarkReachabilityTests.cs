using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using KillerShell.Services;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
public sealed class DemoBookmarkReachabilityTests
{
    [Fact]
    public Task ReopeningDemoFavoritesKeepsTheirFabricatedFoldersAvailable()
        => RuntimeComponentProcess.Run(nameof(DemoBookmarkReachabilityScenario));
}

public static class DemoBookmarkReachabilityScenario
{
    public static void Run()
    {
        System.IO.Packaging.PackUriHelper.Create(new Uri("http://localhost/"));
        var app = RuntimeComponentProcess.StartApplication();
        MainWindow? window = null;
        try
        {
            ThemeManager.GetSetting = key => key switch
            {
                "HomeFolder" => Path.GetTempPath(),
                "RememberTabs" => "0",
                "CheckForUpdatesOnStartup" => "0",
                "CloseToTray" => "0",
                _ => null,
            };
            ThemeManager.Initialize();
            MainWindow.DemoMode = true;
            window = new MainWindow();
            var list = (ListBox)window.FindName("BookmarksList");
            var rows = list.Items.Cast<Bookmark>().Where(item => !MainWindow.IsPlaceSentinel(item.Path)).ToArray();
            Assert.NotEmpty(rows);
            Assert.All(rows, row => Assert.False(row.Unreachable));
            var button = (Button)window.FindName("BookmarksBtn");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var elapsed = Stopwatch.StartNew();
            ShellWindowFixture.PumpUntil(() => elapsed.ElapsedMilliseconds >= 3000);
            Assert.All(rows, row => Assert.False(row.Unreachable,
                "Demo favorites must not be dimmed by probing fabricated paths: " + row.Path));
        }
        finally
        {
            MainWindow.DemoMode = false;
            window?.Close();
            app.Shutdown();
        }
    }
}
