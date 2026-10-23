using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using KillerShell.Models;
using KillerShell.Shell;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
public sealed class SearchScopeRuntimeTests
{
    [Fact]
    public Task SearchResultsSurvivePaneSwitchAndEditorSearchPreservesDocument()
        => RuntimeComponentProcess.Run(nameof(SearchScopeRuntimeScenario));

    [Fact]
    public Task ChoosingSearchFolderPreservesTheSourceDocument()
        => RuntimeComponentProcess.Run(nameof(SearchFolderPickerScenario));

    [Fact]
    public Task AllTabsScopeConvertsWslFoldersWithoutAccessingTheDistribution()
        => RuntimeComponentProcess.Run(nameof(SearchWslScopeScenario));
}

public static class SearchScopeRuntimeScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var left = (FilePane)window.FindName("LeftPane");
        var right = (FilePane)window.FindName("RightPane");
        File.WriteAllText(Path.Combine(fixture.Root, "needle.txt"), "sample");
        File.WriteAllText(Path.Combine(fixture.Root, "unrelated.txt"), "sample");

        left.Active.Groups[0].Terms[0].Pattern = "needle";
        var search = left.Active;
        ShellWindowFixture.Click(window, "SearchButton");
        ShellWindowFixture.PumpUntil(() => search.HasSearch && !search.IsSearching && search.Results.Count == 1);
        Assert.False(search.IsBrowsing);
        Assert.Equal("needle.txt", search.Results[0].FileName);

        ShellWindowFixture.Click(window, "DualPaneRailBtn");
        ShellWindowFixture.PumpUntil(() => right.IsVisible && right.Active.IsBrowsing);
        window.FocusPane(right);
        window.FocusPane(left);
        ShellWindowFixture.PumpUntil(() => ReferenceEquals(left.ResultsList.ItemsSource, search.Results));
        Assert.Single(search.Results);

        ShellWindowFixture.Click(window, "NewDocRailBtn");
        var document = left.Active;
        Assert.NotNull(document.Editor);
        document.Groups[0].Terms[0].Pattern = "needle";
        string editorFolder = Path.Combine(fixture.Root, "editor-search");
        Directory.CreateDirectory(editorFolder);
        File.WriteAllText(Path.Combine(editorFolder, "needle.txt"), "sample");
        document.CurrentFolder = editorFolder;
        left.RootPathBox.Text = editorFolder;
        ShellWindowFixture.Click(window, "SearchButton");
        ShellWindowFixture.PumpUntil(() => !ReferenceEquals(left.Active, document) && left.Active.HasSearch
            && !left.Active.IsSearching && left.Active.Results.Count == 1);
        Assert.Contains(document, left.Tabs);
        Assert.NotNull(document.Editor);
        Assert.Null(left.Active.Editor);
        Assert.Equal(Visibility.Visible, left.ResultsList.Visibility);
        Assert.NotSame(document.Groups[0], left.Active.Groups[0]);
        Assert.Equal("editor-search", left.Active.Title);
        Assert.Equal(MainWindow.TabKindTitle(left.Active), ((TextBlock)((ContentControl)window.FindName("TitleContextHost")).Content).Text);
    }
}

public static class SearchFolderPickerScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = (FilePane)window.FindName("LeftPane");
        File.WriteAllText(Path.Combine(fixture.Root, "needle.txt"), "sample");
        ShellWindowFixture.Click(window, "NewDocRailBtn");
        var document = pane.Active;
        var editor = document.Editor;
        string title = document.Title;
        string originalFolder = Path.Combine(fixture.Root, "missing-folder");
        document.CurrentFolder = originalFolder;
        pane.RootPathBox.Text = string.Empty;
        document.Groups[0].Terms[0].Pattern = "needle";

        EventManager.RegisterClassHandler(typeof(FolderPickerDialog), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => ((FolderPickerDialog)sender).Opacity = 0));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        bool accepted = false;
        bool cancel = true;
        timer.Tick += (_, _) =>
        {
            var picker = Application.Current.Windows.OfType<FolderPickerDialog>().FirstOrDefault();
            if (picker == null || !picker.NavigationTask.IsCompleted) return;
            timer.Stop();
            if (cancel) { picker.DialogResult = false; return; }
            ((TextBox)picker.FindName("PathBox")).Text = fixture.Root;
            var select = Descendants(picker).OfType<Button>().Single(button =>
                Equals(button.Content, Application.Current.FindResource("Str_Btn_Select")));
            accepted = true;
            select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        try
        {
            timer.Start();
            ShellWindowFixture.Click(window, "SearchButton");
            Assert.Same(document, pane.Active);
            Assert.Same(editor, document.Editor);
            Assert.False(document.IsBrowsing);
            Assert.Equal(title, document.Title);
            Assert.Equal(originalFolder, document.CurrentFolder);
            Assert.Equal(string.Empty, document.RootPath);
            cancel = false;
            timer.Start();
            ShellWindowFixture.Click(window, "SearchButton");
            ShellWindowFixture.PumpUntil(() => !ReferenceEquals(pane.Active, document)
                && pane.Active.HasSearch && !pane.Active.IsSearching && pane.Active.Results.Count == 1);
            Assert.True(accepted);
            Assert.Same(editor, document.Editor);
            Assert.False(document.IsBrowsing);
            Assert.Equal(title, document.Title);
            Assert.Equal(originalFolder, document.CurrentFolder);
            Assert.Equal(string.Empty, document.RootPath);
            Assert.Equal(fixture.Root, pane.Active.RootPath);
            Assert.Equal(Path.GetFileName(fixture.Root), pane.Active.Title);
        }
        finally { timer.Stop(); }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}

public static class SearchWslScopeScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = (FilePane)window.FindName("LeftPane");
        pane.Tabs.Add(new SearchTab("Demo Linux")
        {
            Term = new TerminalControl(TerminalSkin.Default), LaunchProfile = TerminalProfile.Wsl("DemoDistro"),
            RootPath = "/home/demo", CurrentFolder = "/home/demo"
        });
        MainWindow.DemoMode = true;
        try
        {
            var button = (Button)window.FindName("SearchPanelBtn");
            button.ContextMenu.IsOpen = true;
            ((MenuItem)button.ContextMenu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            button.ContextMenu.IsOpen = false;
            string roots = window.TitleSearchRootsText();
            Assert.Contains(@"\\wsl.localhost\DemoDistro\home\demo", roots);
        }
        finally { MainWindow.DemoMode = false; }
    }
}
