using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Shell;
using KillerShell.Tests.Services;
using Xunit;

namespace KillerShell.Tests.Terminal;

public sealed class OperationStatusLocalizationTests
{
    [Fact]
    public Task CopyAndArchiveCountsRelocalizeInBothPanesAndClearOnTheNextStatus()
        => RuntimeComponentProcess.Run(nameof(OperationStatusLocalizationScenario));
}

public static class OperationStatusLocalizationScenario
{
    public static void Run()
    {
        using var files = new FileArchiveLinkFixture();
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var left = (FilePane)window.FindName("LeftPane");
        var right = (FilePane)window.FindName("RightPane");
        ShellWindowFixture.Click(window, "DualPaneRailBtn");
        ShellWindowFixture.PumpUntil(() => right.Active.IsBrowsing);
        var copyTab = new SearchTab("Copy results") { IsSearching = true };
        var archiveTab = new SearchTab("Archive results");
        left.Tabs.Add(copyTab);
        right.Tabs.Add(archiveTab);
        left.Active = copyTab;
        right.Active = archiveTab;
        window.FocusPane(left);
        Complete(window.DropOntoFolder(new[] { files.Source }, files.Target, DragDropEffects.Copy, true, false));
        Assert.Equal("Done - 1 item(s) 1 directory link(s) skipped.", copyTab.StatusMessage);
        window.FocusPane(right);
        Complete(window.ArchiveAdd(new[] { files.Source }, files.Archive, ""));
        Assert.Equal("Added 1 to the archive 1 directory link(s) skipped.", archiveTab.StatusMessage);

        Switch(window, Locale.Fr);
        Assert.Equal("Terminé - 1 élément(s) Liens vers des dossiers ignorés : 1.", copyTab.StatusMessage);
        Assert.Equal("1 ajouté(s) à l'archive Liens vers des dossiers ignorés : 1.", archiveTab.StatusMessage);
        Assert.Equal("Str_Status_OpDone", copyTab.StatusKey);
        Assert.Equal("Str_Status_ArchiveAdded", archiveTab.StatusKey);
        VerifyFooter(window, archiveTab, "OkBrush");
        window.FocusPane(left);
        VerifyFooter(window, copyTab, "WarnBrush");

        // A later normal operation must replace both the main message and its supplement.
        Complete(window.DropOntoFolder(new[] { files.File }, files.Target, DragDropEffects.Copy, true, false));
        Assert.Equal("Terminé - 1 élément(s)", copyTab.StatusMessage);
        ShellWindowFixture.Click(window, "DualPaneRailBtn");
        Switch(window, Locale.De);
        Assert.Equal("Fertig - 1 Element(e)", copyTab.StatusMessage);
        Assert.Equal("1 zum Archiv hinzugefügt Übersprungene Ordnerverknüpfungen: 1.", archiveTab.StatusMessage);
        ShellWindowFixture.Click(window, "DualPaneRailBtn");
        window.FocusPane(right);
        VerifyFooter(window, archiveTab, "OkBrush");

        Complete(window.ArchiveAdd(new[] { files.File }, files.Archive, ""));
        Assert.Equal("1 zum Archiv hinzugefügt", archiveTab.StatusMessage);
        Switch(window, Locale.Fr);
        Assert.Equal("1 ajouté(s) à l'archive", archiveTab.StatusMessage);
        VerifyFooter(window, archiveTab, "OkBrush");
        copyTab.IsSearching = false;
        Assert.Equal("fixture", File.ReadAllText(files.File));
    }

    private static void Complete(Task command)
    {
        ShellWindowFixture.PumpUntil(() => command.IsCompleted);
        command.GetAwaiter().GetResult();
    }

    private static void Switch(MainWindow window, Locale locale)
    {
        ShellWindowFixture.Click(window, "LangButton");
        var menu = (ContextMenu)window.FindName("LangMenu");
        var columns = (StackPanel)menu.Items[0];
        columns.Children.OfType<StackPanel>().SelectMany(column => column.Children.OfType<RadioButton>())
            .Single(button => Equals(button.Tag, locale.ToString())).IsChecked = true;
        menu.IsOpen = false;
    }

    private static void VerifyFooter(MainWindow window, SearchTab tab, string tone)
    {
        window.UpdateLayout();
        Assert.Equal(tab.StatusMessage, ((TextBlock)window.FindName("StatusText")).Text);
        Assert.Equal(((SolidColorBrush)window.FindResource(tone)).Color,
            ((SolidColorBrush)((Border)window.FindName("StatusDot")).Background).Color);
    }
}
