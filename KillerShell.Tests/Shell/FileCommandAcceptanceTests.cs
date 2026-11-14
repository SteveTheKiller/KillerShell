using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class FileCommandAcceptanceTests
{
    [Fact]
    public Task ArchiveCardSupportsZipTarMembersFailuresAndSearchSemantics()
        => RuntimeComponentProcess.Run(nameof(ArchiveCardAcceptanceScenario));

    [Fact]
    public Task MixedSelectionCopiesEveryName()
        => RuntimeComponentProcess.Run(nameof(MixedCopyNameScenario));

    [Fact]
    public Task DemoFileCardStillOpensItsEditor()
        => RuntimeComponentProcess.Run(nameof(DemoFileOpenScenario));

    [Fact]
    public Task PhysicalEnterEntersAZipWithoutLaunchingItsAssociation()
        => RuntimeComponentProcess.Run(nameof(ArchiveEnterScenario));
}

public static class ArchiveCardAcceptanceScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var pane = (FilePane)fixture.Window.FindName("LeftPane");
        var opened = new List<string>();
        fixture.Window.OpenAssociatedPath = path => { opened.Add(path); return ShellOpenResult.Opened; };
        string zipPath = Path.Combine(fixture.Root, "archive.zip");
        using (var zip = new ZipArchive(File.Create(zipPath), ZipArchiveMode.Create))
        using (var writer = new StreamWriter(zip.CreateEntry("nested/member.txt").Open()))
            writer.Write("original archive member");
        string ordinary = Path.Combine(fixture.Root, "ordinary.txt");
        File.WriteAllText(ordinary, "ordinary document");
        string tar = Path.Combine(fixture.Root, "archive.tar");
        Assert.True(TarArchiveCreator.Create(tar, new[] { ordinary }, false, null, CancellationToken.None).Ok);
        string unsupported = Path.Combine(fixture.Root, "archive.rar");
        File.WriteAllText(unsupported, "unsupported fixture");
        ShellWindowFixture.PumpUntil(() => pane.Active.Results.Any(row => row.FileName == "archive.rar"));

        Open(pane, new SearchResult { FilePath = zipPath, FileName = "archive.zip" });
        ShellWindowFixture.PumpUntil(() => pane.Active.CurrentFolder == ArchiveProvider.Combine(zipPath, ""));
        Assert.Empty(opened);
        Open(pane, Assert.Single(pane.Active.Results, row => row.FileName == "nested"));
        ShellWindowFixture.PumpUntil(() => pane.Active.CurrentFolder == ArchiveProvider.Combine(zipPath, "nested"));
        Open(pane, Assert.Single(pane.Active.Results, row => row.FileName == "member.txt"));
        ShellWindowFixture.PumpUntil(() => opened.Count == 1);
        string extracted = opened[0];
        try
        {
            Assert.Equal("original archive member", File.ReadAllText(extracted));
            Assert.NotEqual(ArchiveProvider.Combine(zipPath, "nested/member.txt"), extracted);
            File.WriteAllText(extracted, "edited temporary copy");
            using var zip = ZipFile.OpenRead(zipPath);
            using var reader = new StreamReader(zip.GetEntry("nested/member.txt")!.Open());
            Assert.Equal("original archive member", reader.ReadToEnd());
        }
        finally
        {
            string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(extracted).StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The extracted test copy escaped its temporary directory.");
            File.Delete(extracted);
            Directory.Delete(Path.GetDirectoryName(extracted)!);
        }

        Open(pane, new SearchResult { FilePath = ArchiveProvider.Combine(zipPath, "missing.txt"), FileName = "missing.txt" });
        ShellWindowFixture.PumpUntil(() => pane.Active.StatusKey == "Str_Status_ExtractFailed");
        Assert.Single(opened);
        Open(pane, new SearchResult { FilePath = tar, FileName = "archive.tar" });
        ShellWindowFixture.PumpUntil(() => pane.Active.CurrentFolder == ArchiveProvider.Combine(tar, ""));
        Assert.Contains(pane.Active.Results, row => row.FileName == "ordinary.txt");
        Assert.Single(opened);
        Open(pane, new SearchResult { FilePath = unsupported, FileName = "archive.rar" });
        Assert.Equal(unsupported, opened.Last());
        Open(pane, new SearchResult { FilePath = ordinary, FileName = "ordinary.txt" });
        Assert.Equal(ordinary, opened.Last());
        string browseLocation = pane.Active.CurrentFolder;
        pane.Active.IsBrowsing = false;
        Open(pane, new SearchResult { FilePath = zipPath, FileName = "archive.zip" });
        Assert.Equal(zipPath, opened.Last());
        Assert.Equal(browseLocation, pane.Active.CurrentFolder);
        Assert.False(pane.Active.IsBrowsing);
    }

    internal static void Open(FilePane pane, SearchResult row)
    {
        pane.Active.Results.Clear();
        pane.Active.Results.Add(row);
        pane.ResultsList.SelectedItem = row;
        FileCommandCard.Open(pane);
        FileCommandCard.Button(pane, "open").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    }
}

public static class MixedCopyNameScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var pane = (FilePane)fixture.Window.FindName("LeftPane");
        var previous = FileCommandCard.CaptureClipboard();
        bool touched = false;
        try
        {
            var folder = new SearchResult { FilePath = Path.Combine(fixture.Root, "Runbooks"), FileName = "Runbooks", IsDirectory = true };
            var file = new SearchResult { FilePath = Path.Combine(fixture.Root, "readme.txt"), FileName = "readme.txt" };
            pane.Active.Results.Clear();
            pane.Active.Results.Add(folder);
            pane.Active.Results.Add(file);
            pane.ResultsList.SelectedItems.Add(folder);
            pane.ResultsList.SelectedItems.Add(file);
            FileCommandCard.Open(pane);
            var name = FileCommandCard.Button(pane, "name");
            Assert.Equal(Visibility.Visible, name.Visibility);
            Assert.Equal(Visibility.Collapsed, FileCommandCard.Button(pane, "hash").Visibility);
            touched = true;
            name.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.Equal("Runbooks" + Environment.NewLine + "readme.txt", Clipboard.GetText());
        }
        finally { if (touched) FileCommandCard.RestoreClipboard(previous); }
    }
}

public static class DemoFileOpenScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var pane = (FilePane)fixture.Window.FindName("LeftPane");
        const string parent = @"C:\Users\Demo\Documents\Runbooks";
        var file = DemoFs.Children(parent).First(row => !row.IsDir);
        MainWindow.DemoMode = true;
        try
        {
            ArchiveCardAcceptanceScenario.Open(pane, new SearchResult
                { FilePath = Path.Combine(parent, file.Name), FileName = file.Name });
            ShellWindowFixture.PumpUntil(() => pane.Active.IsEditor);
        }
        finally { MainWindow.DemoMode = false; }
    }
}

public static class ArchiveEnterScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var pane = (FilePane)fixture.Window.FindName("LeftPane");
        string archive = Path.Combine(fixture.Root, "enter.zip");
        using (var zip = new ZipArchive(File.Create(archive), ZipArchiveMode.Create))
            zip.CreateEntry("entered.txt");
        ShellWindowFixture.PumpUntil(() => pane.Active.Results.Any(row => row.FilePath == archive));
        pane.ResultsList.SelectedItem = Assert.Single(pane.Active.Results, row => row.FilePath == archive);
        int associations = 0;
        fixture.Window.OpenAssociatedPath = _ => { associations++; return ShellOpenResult.Opened; };
        fixture.Window.WindowState = WindowState.Normal;
        fixture.Window.Left = SystemParameters.WorkArea.Left + 20;
        fixture.Window.Top = SystemParameters.WorkArea.Top + 20;
        fixture.Window.Opacity = 1;
        fixture.Window.Activate();
        pane.ResultsList.Focus();
        Keyboard.Focus(pane.ResultsList);
        ShellWindowFixture.PumpUntil(() => pane.ResultsList.IsKeyboardFocusWithin);
        TerminalAppsReleaseScenario.Stroke(fixture.Window, 0x0D, Key.Enter);
        ShellWindowFixture.PumpUntil(() => pane.Active.CurrentFolder == ArchiveProvider.Combine(archive, ""));
        // The folder changes first and the archive's listing lands a moment later.
        ShellWindowFixture.PumpUntil(() => pane.Active.Results.Any(row => row.FileName == "entered.txt"),
            diagnostic: () => "rows=" + string.Join(",", pane.Active.Results.Select(row => row.FileName)));
        Assert.Equal(0, associations);
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
}
