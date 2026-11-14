using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using KillerShell.Editing;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
public sealed class TabCardMetadataTests
{
    [Fact]
    public Task CardMetadataDisplaysLocationsWithoutInternalPathMarkers()
        => RuntimeComponentProcess.Run(nameof(TabCardMetadataScenario));
}

public static class TabCardMetadataScenario
{
    public static void Run()
    {
        System.IO.Packaging.PackUriHelper.Create(new Uri("application:///"));
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
            window = new MainWindow();
            window.TabIconPathExists = (_, _) => false;
            var tab = ((FilePane)window.FindName("LeftPane")).Active;
            tab.IsBrowsing = true;
            tab.Title = "Location title?";
            var paths = new[]
            {
                ":ThisPC:", ":RecycleBin:", @"C:\docs\bundle.zip?inside/folder",
                @"C:\docs\ordinary", @"C:\docs\bundle.zip", "custom:location",
            };
            var actual = paths.Select(path =>
            {
                tab.CurrentFolder = path;
                return Metadata(window, tab);
            }).ToArray();
            Assert.Equal(new[]
            {
                "", "", @"C:\docs\bundle.zip\inside\folder",
                @"C:\docs\ordinary", @"C:\docs\bundle.zip", "custom:location",
            }, actual);

            tab.HasSearch = true;
            tab.QueryLabel = "name:query?text";
            Assert.Equal("name:query?text", Metadata(window, tab));

            const string document = @"C:\docs\bundle.zip?inside/note.md";
            tab.Editor = new EditorControl(document);
            Assert.Equal(document, Metadata(window, tab));
            tab.Editor = new EditorControl(string.Empty);
            Assert.Equal(string.Empty, Metadata(window, tab));
            tab.Editor = null;
        }
        finally
        {
            window?.Close();
            app.Shutdown();
        }
    }

    private static string Metadata(MainWindow window, SearchTab tab)
    {
        var card = new ContextMenu { Tag = "tabcard", PlacementTarget = new Border { DataContext = tab } };
        window.TabPlacementMenu_Opened(card, new RoutedEventArgs());
        var head = Assert.Single(card.Items.OfType<MenuItem>(), item => item.Tag as string == "cardhead");
        Assert.Equal("Location title?", AutomationProperties.GetName(head));
        var header = Assert.IsType<DockPanel>(head.Header);
        var text = Assert.Single(header.Children.OfType<StackPanel>());
        var labels = text.Children.OfType<TextBlock>().Select(label => label.Text).ToArray();
        Assert.Equal("Location title?", labels[0]);
        return labels.Length == 1 ? string.Empty : Assert.Single(labels.Skip(1));
    }
}
