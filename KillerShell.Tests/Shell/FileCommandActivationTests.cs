using System;
using System.Linq;
using System.IO;
using System.IO.Compression;
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
public sealed class FileCommandActivationTests
{
    [Fact]
    public Task CardOpenEntersAVirtualArchiveFolder()
        => RuntimeComponentProcess.Run(nameof(ArchiveFolderCardScenario));

    [Fact]
    public Task MissingSelectionItemDoesNotOpenAPartialWindowsMenu()
        => RuntimeComponentProcess.Run(nameof(MissingSelectionMenuScenario));

    [Fact]
    public Task FolderCardCopiesItsName()
        => RuntimeComponentProcess.Run(nameof(FolderCopyNameScenario));

    [Fact]
    public Task MixedParentsExplainWhyTheWindowsMenuIsUnavailable()
        => RuntimeComponentProcess.Run(nameof(MixedParentMenuScenario));
}

public static class MixedParentMenuScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var pane = (FilePane)fixture.Window.FindName("LeftPane");
        string first = Directory.CreateDirectory(Path.Combine(fixture.Root, "A", "one")).FullName;
        string second = Directory.CreateDirectory(Path.Combine(fixture.Root, "B", "two")).FullName;
        ShellWindowFixture.PumpUntil(() => pane.Active.Results.Any(row => row.FileName == "A")
            && pane.Active.Results.Any(row => row.FileName == "B"));
        pane.Active.Results.Clear();
        var a = new SearchResult { FilePath = first, FileName = "one", IsDirectory = true };
        var b = new SearchResult { FilePath = second, FileName = "two", IsDirectory = true };
        pane.Active.Results.Add(a);
        pane.Active.Results.Add(b);
        pane.ResultsList.SelectedItems.Add(a);
        pane.ResultsList.SelectedItems.Add(b);
        int nativeCalls = 0;
        fixture.Window.ShowWindowsMenu = (_, _) => { nativeCalls++; return true; };
        FileCommandCard.Open(pane);
        var menu = pane.ResultsList.ContextMenu;
        FileCommandCard.Click(pane, "shellmenu");
        menu.IsOpen = false;
        ShellWindowFixture.PumpUntil(() => nativeCalls > 0 || pane.Active.StatusKey == "Str_Status_ShellSelectionNeedsOneFolder");
        Assert.Equal("Str_Status_ShellSelectionNeedsOneFolder", pane.Active.StatusKey);
        Assert.Equal(0, nativeCalls);
    }
}

public static class FolderCopyNameScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var pane = (FilePane)fixture.Window.FindName("LeftPane");
        var previous = FileCommandCard.CaptureClipboard();
        bool touched = false;
        try
        {
            pane.Active.Results.Clear();
            var folder = new SearchResult { FilePath = Path.Combine(fixture.Root, "Runbooks"), FileName = "Runbooks", IsDirectory = true };
            pane.Active.Results.Add(folder);
            pane.ResultsList.SelectedItem = folder;
            FileCommandCard.Open(pane);
            Assert.True(FileCommandCard.Offers(pane, "name"));
            touched = true;
            FileCommandCard.Click(pane, "name");
            Assert.Equal("Runbooks", Clipboard.GetText());
        }
        finally
        {
            if (touched) FileCommandCard.RestoreClipboard(previous);
        }
    }
}

public static class MissingSelectionMenuScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var pane = (FilePane)fixture.Window.FindName("LeftPane");
        string existingPath = Path.Combine(fixture.Root, "existing.txt");
        File.WriteAllText(existingPath, "fixture");
        ShellWindowFixture.PumpUntil(() => pane.Active.Results.Any(row => row.FileName == "existing.txt"));
        var existing = new SearchResult { FilePath = existingPath, FileName = "existing.txt" };
        var missing = new SearchResult { FilePath = Path.Combine(fixture.Root, "missing.txt"), FileName = "missing.txt" };
        pane.Active.Results.Clear();
        pane.Active.Results.Add(existing);
        pane.Active.Results.Add(missing);
        pane.ResultsList.SelectedItems.Add(existing);
        pane.ResultsList.SelectedItems.Add(missing);
        int nativeCalls = 0;
        fixture.Window.ShowWindowsMenu = (_, _) => { nativeCalls++; return true; };
        FileCommandCard.Open(pane);
        var menu = pane.ResultsList.ContextMenu;
        FileCommandCard.Click(pane, "shellmenu");
        menu.IsOpen = false;
        ShellWindowFixture.PumpUntil(() => nativeCalls > 0 || pane.Active.StatusKey == "Str_Status_ShellFailed");
        Assert.Equal(0, nativeCalls);
        Assert.Equal("Str_Status_ShellFailed", pane.Active.StatusKey);
    }
}

public static class ArchiveFolderCardScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var pane = (FilePane)fixture.Window.FindName("LeftPane");
            string archive = Path.Combine(fixture.Root, "archive.zip");
            using (var zip = new ZipArchive(File.Create(archive), ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("nested/member.txt").Open()))
                writer.Write("harmless archive content");
            string target = ArchiveProvider.Combine(archive, "nested");
            pane.Active.Results.Clear();
            var row = new SearchResult { FilePath = target, FileName = "nested", IsDirectory = true };
            pane.Active.Results.Add(row);
            pane.ResultsList.SelectedItem = row;
            FileCommandCard.Open(pane);
            FileCommandCard.Button(pane, "open").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            ShellWindowFixture.PumpUntil(() => pane.Active.CurrentFolder == target);
    }
}

internal static class FileCommandCard
{
    internal static IDataObject? CaptureClipboard()
    {
        var current = Clipboard.GetDataObject();
        if (current == null) return null;
        var copy = new DataObject();
        int unreadable = 0;
        foreach (string format in current.GetFormats(false))
        {
            try
            {
                object? value = current.GetData(format, false);
                if (value != null) copy.SetData(format, value, false);
            }
            catch (System.Runtime.InteropServices.COMException error)
                when (unchecked((uint)error.HResult) == 0x800401D3u)
            {
                unreadable++;
            }
        }
        string? evidence = Environment.GetEnvironmentVariable("KILLERSHELL_CLIPBOARD_CAPTURE_EVIDENCE");
        if (!string.IsNullOrEmpty(evidence))
            File.AppendAllText(evidence, "unreadable=" + unreadable + "; HRESULT=0x800401D3" + Environment.NewLine);
        return copy;
    }

    internal static void RestoreClipboard(IDataObject? previous)
    {
        Exception? failure = null;
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (previous != null) Clipboard.SetDataObject(previous, true);
                else Clipboard.Clear();
                return;
            }
            catch (System.Runtime.InteropServices.COMException error)
            {
                failure = error;
                System.Threading.Thread.Sleep(50);
            }
        }
        throw new InvalidOperationException("The original clipboard could not be restored.", failure);
    }

    internal static void Open(FilePane pane)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(pane),
            Environment.TickCount, Key.Apps) { RoutedEvent = Keyboard.KeyUpEvent, Source = pane.ResultsList };
        InputManager.Current.ProcessInput(args);
        ShellWindowFixture.PumpUntil(() => pane.ResultsList.ContextMenu.IsOpen);
    }

    /// <summary>A tile on the file card, by its command name.</summary>
    internal static Button Button(FilePane pane, string command)
        => Assert.IsType<Button>(Command(pane, command));

    /// <summary>A command on the file card by name: a tile, or a row on one of its flyout tiles.</summary>
    internal static FrameworkElement Command(FilePane pane, string command)
        => Assert.Single(KillerShell.Controls.CardMenus.Commands(pane.ResultsList.ContextMenu),
            element => (element is Button button ? button.CommandParameter : element.Tag) as string == command);

    /// <summary>Whether the file card offers the command at all.</summary>
    internal static bool Offers(FilePane pane, string command)
        => KillerShell.Controls.CardMenus.Commands(pane.ResultsList.ContextMenu)
            .Any(element => (element is Button button ? button.CommandParameter : element.Tag) as string == command);

    /// <summary>Runs a file card command the way a click does.</summary>
    internal static void Click(FilePane pane, string command)
    {
        var element = Command(pane, command);
        element.RaiseEvent(new RoutedEventArgs(element is MenuItem ? MenuItem.ClickEvent : ButtonBase.ClickEvent));
    }
}
