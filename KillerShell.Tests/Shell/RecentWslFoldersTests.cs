using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using KillerShell.Services;
using KillerShell.Shell;
using KillerShell.Tests.Editing;
using KillerShell.Tests.Terminal;
using Xunit;

namespace KillerShell.Tests.Shell;

public sealed class RecentWslFoldersTests
{
    [Fact]
    public Task RecentsKeepWslFoldersInSavedOrderWhileDroppingMissingOrdinaryFolders()
        => EditorScenarioProcess.Run("KillerShell.Tests.Shell.RecentWslFoldersScenario");
}

public static class RecentWslFoldersScenario
{
    public static void Run()
    {
        System.IO.Packaging.PackUriHelper.Create(new Uri("http://localhost/"));
        var app = RuntimeComponentProcess.StartApplication();
        MainWindow? window = null;
        string root = Path.Combine(Path.GetTempPath(), "ks-recents-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousGet = ThemeManager.GetSetting;
        var previousSet = ThemeManager.SetSetting;
        try
        {
            string wsl = @"\\wsl.localhost\KillerShell-Fixture-" + Guid.NewGuid().ToString("N") + @"\home\agent";
            string missing = Path.Combine(root, "missing");
            Assert.False(Directory.Exists(wsl), "The unique nonexistent distribution must not accidentally exist.");
            string saved = string.Join("|", new[] { wsl, root, missing });
            ThemeManager.GetSetting = key => key switch
            {
                "RecentFolders" => saved,
                "HomeFolder" => root,
                "RememberTabs" => "0",
                "CheckForUpdatesOnStartup" => "0",
                "CloseToTray" => "0",
                _ => null,
            };
            ThemeManager.SetSetting = (_, _) => { };
            ThemeManager.Initialize();
            window = new MainWindow();
            var pane = (FilePane)window.FindName("LeftPane");
            try
            {
                window.Recents_Click(pane);
                var paths = pane.RecentsMenu.Items.OfType<MenuItem>()
                    .Select(item => item.ToolTip as string).Where(path => path != null).ToArray();
                Assert.Contains(wsl, paths);
                Assert.Equal(new[] { wsl, root }, paths);
                Assert.DoesNotContain(missing, paths);
            }
            finally { pane.RecentsMenu.IsOpen = false; }
        }
        finally
        {
            window?.Close();
            ThemeManager.GetSetting = previousGet;
            ThemeManager.SetSetting = previousSet;
            Directory.Delete(root, true);
            app.Shutdown();
        }
    }
}
