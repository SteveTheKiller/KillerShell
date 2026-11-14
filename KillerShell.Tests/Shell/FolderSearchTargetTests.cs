using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using KillerShell.Models;
using KillerShell.Shell;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class FolderSearchTargetTests
{
    [Fact]
    public Task SavedPlaceSearchReplacesPipedFilesWithTheChosenFolder()
        => RuntimeComponentProcess.Run(nameof(SavedPlaceFolderSearchScenario));

    [Fact]
    public Task SavedPlaceSearchReplacesAllTabsScopeWithTheChosenFolder()
        => RuntimeComponentProcess.Run(nameof(SavedPlaceAllTabsSearchScenario));

    [Fact]
    public Task SavedPlaceSearchUsesTheChosenFolderAndPreservesTheSourceDocument()
        => RuntimeComponentProcess.Run(nameof(SavedPlaceEditorSearchScenario));

    [Fact]
    public Task ResultFolderSearchReplacesAllTabsScopeWithTheChosenFolder()
        => RuntimeComponentProcess.Run(nameof(ResultFolderSearchScopeScenario));

    [Fact]
    public Task SavedPlaceSearchPreservesTheLiveTerminalAndItsWorkingFolder()
        => RuntimeComponentProcess.Run(nameof(SavedPlaceTerminalSearchScenario));
}

public static class SavedPlaceTerminalSearchScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = window.Pane;
        string folderA = Path.Combine(fixture.Root, "A");
        string folderB = Path.Combine(fixture.Root, "B");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);
        string fileA = Path.Combine(folderA, "needle-A.txt");
        File.WriteAllText(fileA, "sample");
        File.WriteAllText(Path.Combine(folderB, "needle-B.txt"), "sample");
        var profile = TerminalProfile.Cmd();
        window.OpenShell(profile, folderB);
        var source = pane.Active;
        var terminal = source.Term!;
        ShellWindowFixture.PumpUntil(() =>
        {
            terminal.ProcessOutput();
            return Lines(terminal).Any(line => line.Contains(folderB));
        });
        source.Groups[0].Terms[0].Pattern = "needle";
        string title = source.Title;

        SavedPlaceFolderSearchScenario.ChooseSavedPlaceSearch(window, folderA);
        ShellWindowFixture.Click(window, "SearchButton");
        ShellWindowFixture.PumpUntil(() => !ReferenceEquals(source, pane.Active)
            && pane.Active.HasSearch && !pane.Active.IsSearching);
        Assert.Equal(fileA, Assert.Single(pane.Active.Results).FilePath);
        Assert.Contains(source, pane.Tabs);
        Assert.Same(terminal, source.Term);
        Assert.Same(profile, source.LaunchProfile);
        Assert.Equal(folderB, source.CurrentFolder);
        Assert.Equal(folderB, source.RootPath);
        Assert.Equal(title, source.Title);
        Assert.Equal("needle", source.Groups[0].Terms[0].Pattern);
        Assert.NotSame(source.Groups[0], pane.Active.Groups[0]);
        terminal.Send("echo SCOPE_SESSION_ALIVE\r");
        ShellWindowFixture.PumpUntil(() =>
        {
            terminal.ProcessOutput();
            return Lines(terminal).Any(line => line.Trim() == "SCOPE_SESSION_ALIVE");
        });
    }

    private static IEnumerable<string> Lines(TerminalControl terminal)
        => Enumerable.Range(0, terminal.Buffer.TotalLines)
            .Select(index => string.Concat(terminal.Buffer.LineAt(index).Select(cell => cell.Text)));
}

public static class ResultFolderSearchScopeScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = window.Pane;
        string folderA = Path.Combine(fixture.Root, "A");
        string folderB = Path.Combine(fixture.Root, "B");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);
        string fileA = Path.Combine(folderA, "needle-A.txt");
        File.WriteAllText(fileA, "sample");
        File.WriteAllText(Path.Combine(folderB, "needle-B.txt"), "sample");
        pane.Active.Groups[0].Terms[0].Pattern = "needle";
        pane.Tabs.Add(new SearchTab("Other folder B") { RootPath = folderB, CurrentFolder = folderB });
        var scopeMenu = ((Button)window.FindName("SearchPanelBtn")).ContextMenu;
        scopeMenu.IsOpen = true;
        ((MenuItem)scopeMenu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        scopeMenu.IsOpen = false;
        pane.Active.Results.Clear();
        var folder = new SearchResult { FilePath = folderA, FileName = "A", Directory = fixture.Root, IsDirectory = true };
        pane.Active.Results.Add(folder);
        pane.ResultsList.SelectedItem = folder;
        InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(pane), 0, Key.Apps)
        {
            RoutedEvent = Keyboard.KeyUpEvent, Source = pane.ResultsList
        });
        var menu = pane.ResultsList.ContextMenu;
        ShellWindowFixture.PumpUntil(() => menu.IsOpen);
        menu.Items.OfType<MenuItem>().Single(item => item.InputGestureText == "F3")
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.IsOpen = false;
        ShellWindowFixture.Click(window, "SearchButton");
        ShellWindowFixture.PumpUntil(() => pane.Active.HasSearch && !pane.Active.IsSearching);
        Assert.Equal(fileA, Assert.Single(pane.Active.Results).FilePath);
        Assert.Equal(folderA, window.TitleSearchRootsText());
    }
}

public static class SavedPlaceEditorSearchScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = window.Pane;
        string folderA = Path.Combine(fixture.Root, "A");
        string folderB = Path.Combine(fixture.Root, "B");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);
        string fileA = Path.Combine(folderA, "needle-A.txt");
        File.WriteAllText(fileA, "sample");
        File.WriteAllText(Path.Combine(folderB, "needle-B.txt"), "sample");
        ShellWindowFixture.Click(window, "NewDocRailBtn");
        var document = pane.Active;
        var editor = document.Editor!;
        editor.Text = "Unsaved document stays here";
        document.CurrentFolder = document.RootPath = folderB;
        pane.RootPathBox.Text = folderB;
        document.Groups[0].Terms[0].Pattern = "needle";
        ((TextBox)window.FindName("IncludePatternsBox")).Text = "*.txt";
        ((TextBox)window.FindName("ExcludePatternsBox")).Text = "skip*";
        ((CheckBox)window.FindName("CaseSensitiveCheck")).IsChecked = true;
        var filter = new SearchFilter { FieldIndex = SearchFilter.FieldExt, Text = "txt" };
        document.Filters.Clear();
        document.Filters.Add(filter);
        string title = document.Title;

        try
        {
            SavedPlaceFolderSearchScenario.ChooseSavedPlaceSearch(window, folderA);
            ShellWindowFixture.Click(window, "SearchButton");
            ShellWindowFixture.PumpUntil(() => !ReferenceEquals(pane.Active, document)
                && pane.Active.HasSearch && !pane.Active.IsSearching);
            Assert.Equal(fileA, Assert.Single(pane.Active.Results).FilePath);
            Assert.Contains(document, pane.Tabs);
            Assert.Same(editor, document.Editor);
            Assert.Equal("Unsaved document stays here", editor.Text);
            Assert.Equal(folderB, document.CurrentFolder);
            Assert.Equal(folderB, document.RootPath);
            Assert.Equal(title, document.Title);
            Assert.True(editor.IsModified);
            Assert.Equal("needle", document.Groups[0].Terms[0].Pattern);
            Assert.Equal("needle", pane.Active.Groups[0].Terms[0].Pattern);
            Assert.NotSame(document.Groups[0], pane.Active.Groups[0]);
            Assert.Equal("*.txt", document.IncludePatterns);
            Assert.Equal("*.txt", pane.Active.IncludePatterns);
            Assert.Equal("skip*", document.ExcludePatterns);
            Assert.Equal("skip*", pane.Active.ExcludePatterns);
            Assert.True(document.CaseSensitive);
            Assert.True(pane.Active.CaseSensitive);
            Assert.Same(filter, Assert.Single(document.Filters));
            var clonedFilter = Assert.Single(pane.Active.Filters);
            Assert.NotSame(filter, clonedFilter);
            Assert.Equal(SearchFilter.FieldExt, clonedFilter.FieldIndex);
            Assert.Equal("txt", clonedFilter.Text);
        }
        finally { editor.IsModified = false; }
    }
}

public static class SavedPlaceAllTabsSearchScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = window.Pane;
        string folderA = Path.Combine(fixture.Root, "A");
        string folderB = Path.Combine(fixture.Root, "B");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);
        string fileA = Path.Combine(folderA, "needle-A.txt");
        File.WriteAllText(fileA, "sample");
        File.WriteAllText(Path.Combine(folderB, "needle-B.txt"), "sample");
        pane.Active.Groups[0].Terms[0].Pattern = "needle";
        pane.Tabs.Add(new SearchTab("Other folder B") { RootPath = folderB, CurrentFolder = folderB });
        var scopeMenu = ((Button)window.FindName("SearchPanelBtn")).ContextMenu;
        scopeMenu.IsOpen = true;
        ((MenuItem)scopeMenu.Items[1]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        scopeMenu.IsOpen = false;

        SavedPlaceFolderSearchScenario.ChooseSavedPlaceSearch(window, folderA);
        ShellWindowFixture.Click(window, "SearchButton");
        ShellWindowFixture.PumpUntil(() => pane.Active.HasSearch && !pane.Active.IsSearching);
        Assert.Equal(fileA, Assert.Single(pane.Active.Results).FilePath);
        Assert.Equal(folderA, window.TitleSearchRootsText());
        Assert.Equal("needle", pane.Active.Groups[0].Terms[0].Pattern);
    }
}

public static class SavedPlaceFolderSearchScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var pane = window.Pane;
        string folderA = Path.Combine(fixture.Root, "A");
        string folderB = Path.Combine(fixture.Root, "B");
        Directory.CreateDirectory(folderA);
        Directory.CreateDirectory(folderB);
        string fileA = Path.Combine(folderA, "needle-A.txt");
        string fileB = Path.Combine(folderB, "needle-B.txt");
        File.WriteAllText(fileA, "sample");
        File.WriteAllText(fileB, "sample");
        pane.Active.Groups[0].Terms[0].Pattern = "needle";
        pane.Active.PipeFiles = new List<string> { fileB };
        pane.Active.PipeLabel = "Old B results";
        pane.Active.PipeArgs = new object[] { 1, "B", "needle" };

        ChooseSavedPlaceSearch(window, folderA);
        ShellWindowFixture.Click(window, "SearchButton");
        ShellWindowFixture.PumpUntil(() => pane.Active.HasSearch && !pane.Active.IsSearching);
        Assert.Equal(fileA, Assert.Single(pane.Active.Results).FilePath);
        Assert.Null(pane.Active.PipeFiles);
        Assert.Equal(string.Empty, pane.Active.PipeLabel);
        Assert.Null(pane.Active.PipeArgs);
        Assert.Equal("needle", pane.Active.Groups[0].Terms[0].Pattern);
    }

    internal static void ChooseSavedPlaceSearch(MainWindow window, string folder)
    {
        var list = (ListBox)window.FindName("BookmarksList");
        list.ItemsSource = new[] { new Bookmark { Path = folder } };
        ShellWindowFixture.Click(window, "BookmarksBtn");
        Border? row = null;
        ShellWindowFixture.PumpUntil(() =>
        {
            window.UpdateLayout();
            row = Descendants(list).OfType<Border>().FirstOrDefault(border => border.ContextMenu != null
                && border.DataContext is Bookmark place && place.Path == folder);
            return row != null;
        });
        var menu = row!.ContextMenu;
        menu.PlacementTarget = row;
        menu.IsOpen = true;
        var quick = menu.Items.OfType<MenuItem>().Single(item => item.Tag as string == "cardquick");
        var search = TabCardScenario.Buttons(quick).Single(button => button.CommandParameter as string == "search");
        search.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        ShellWindowFixture.PumpUntil(() => !menu.IsOpen);
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
