using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Shell;
using KillerShell.Tests.Editing;
using KillerShell.Tests.Terminal;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class FileArchiveDirectoryLinksTests
{
    [Theory]
    [InlineData("archive", false)]
    [InlineData("archive", true)]
    [InlineData("copy", false)]
    [InlineData("copy", true)]
    public Task DirectoryLinksAreSkippedWithoutReadingTheirTargets(string operation, bool rootLink)
        => EditorScenarioProcess.Run(typeof(FileArchiveDirectoryLinksScenario).FullName!, operation + ":" + rootLink);

    [Theory]
    [InlineData("archive", false)]
    [InlineData("archive", true)]
    [InlineData("copy", false)]
    [InlineData("copy", true)]
    [InlineData("compress", false)]
    [InlineData("compress", true)]
    [InlineData("copy-from-archive", false)]
    [InlineData("copy-from-archive", true)]
    public Task LoadedCommandsReportTheSkippedLinkCount(string operation, bool rootLink)
        => EditorScenarioProcess.Run(typeof(FileArchiveLinkCommandScenario).FullName!, operation + ":" + rootLink);

    [Theory]
    [InlineData("archive")]
    [InlineData("copy")]
    public Task CancellationRetainsTheSkippedCount(string operation)
        => EditorScenarioProcess.Run(typeof(FileArchiveLinkCancellationScenario).FullName!, operation);

    [Theory]
    [InlineData("archive-pre")]
    [InlineData("copy-pre")]
    [InlineData("archive-work")]
    [InlineData("move")]
    public Task CancellationAndMoveControlsPreserveTheLinkTarget(string operation)
        => EditorScenarioProcess.Run(typeof(FileArchiveLinkControlScenario).FullName!, operation);
}

public static class FileArchiveLinkControlScenario
{
    public static void Run(string operation)
    {
        using var fixture = new FileArchiveLinkFixture();
        using var cancel = new CancellationTokenSource();
        byte[] original = File.ReadAllBytes(fixture.Archive);
        if (operation.EndsWith("-pre", StringComparison.Ordinal)) cancel.Cancel();
        if (operation.StartsWith("archive", StringComparison.Ordinal))
        {
            var result = ArchiveWriter.Add(fixture.Archive, "", new[] { fixture.Source }, ArchiveCollision.Report,
                (_, _, _) => cancel.Cancel(), cancel.Token);
            Assert.False(result.Ok);
            Assert.Equal("Str_Status_ArchiveCanceled", result.ErrorKey);
            Assert.Equal(operation == "archive-pre" ? 0 : 1, result.SkippedDirectoryLinks);
        }
        else if (operation == "copy-pre")
        {
            var result = FileOps.CopyOrMove(new[] { fixture.Source }, fixture.Target, false,
                _ => ConflictChoice.Cancel, _ => { }, cancel.Token);
            Assert.True(result.Canceled);
            Assert.Equal(0, result.SkippedDirectoryLinks);
            Assert.Empty(Directory.GetFileSystemEntries(fixture.Target));
        }
        else
        {
            string moved = Path.Combine(fixture.Target, "ancestor");
            try
            {
                var result = FileOps.CopyOrMove(new[] { fixture.Link }, fixture.Target, true,
                    _ => ConflictChoice.Cancel, _ => { }, cancel.Token);
                Assert.Empty(result.Failed);
                Assert.Equal(1, result.Succeeded);
                Assert.Equal(0, result.SkippedDirectoryLinks);
                Assert.False(Directory.Exists(fixture.Link));
                Assert.True((File.GetAttributes(moved) & FileAttributes.ReparsePoint) != 0);
                Assert.Equal("fixture", File.ReadAllText(Path.Combine(moved, "fixture.txt")));
            }
            finally { if (Directory.Exists(moved)) Directory.Delete(moved); }
        }
        Assert.Equal(original, File.ReadAllBytes(fixture.Archive));
        Assert.Equal("fixture", File.ReadAllText(fixture.File));
    }
}

public static class FileArchiveLinkCancellationScenario
{
    public static void Run(string operation)
    {
        using var fixture = new FileArchiveLinkFixture();
        using var cancel = new CancellationTokenSource();
        byte[] original = File.ReadAllBytes(fixture.Archive);
        IEnumerable<string> Sources()
        {
            yield return fixture.Link;
            cancel.Cancel();
            yield return fixture.Source;
        }
        if (operation == "archive")
        {
            var result = ArchiveWriter.Add(fixture.Archive, "", Sources(), ArchiveCollision.Report,
                null, cancel.Token);
            Assert.False(result.Ok);
            Assert.Equal("Str_Status_ArchiveCanceled", result.ErrorKey);
            Assert.Equal(1, result.SkippedDirectoryLinks);
        }
        else
        {
            var result = FileOps.CopyOrMove(new[] { fixture.Source }, fixture.Target, false,
                _ => ConflictChoice.Cancel, _ => cancel.Cancel(), cancel.Token);
            Assert.True(result.Canceled);
            Assert.Equal(1, result.SkippedDirectoryLinks);
            Assert.Equal(1, result.Skipped);
        }
        Assert.Equal(original, File.ReadAllBytes(fixture.Archive));
        Assert.Empty(Directory.GetFiles(fixture.Target, "*", SearchOption.AllDirectories));
        Assert.Equal("fixture", File.ReadAllText(fixture.File));
    }
}

public static class FileArchiveLinkCommandScenario
{
    public static void Run(string argument)
    {
        string[] parts = argument.Split(':');
        bool rootLink = bool.Parse(parts[1]);
        using var fixture = new FileArchiveLinkFixture();
        string selected = rootLink ? fixture.Link : fixture.Source;
        Environment.SetEnvironmentVariable("KS_STATE", Path.Combine(fixture.Root, "state.json"));
        var app = RuntimeComponentProcess.StartApplication();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var settings = new Dictionary<string, string>
        {
            ["EscQuit"] = "quit", ["RememberTabs"] = "0", ["HomeFolder"] = fixture.Root,
            ["CheckForUpdatesOnStartup"] = "0", ["CloseToTray"] = "0", ["Language"] = "en-US"
        };
        ThemeManager.GetSetting = key => settings.TryGetValue(key, out string value) ? value : null;
        ThemeManager.SetSetting = (key, value) => settings[key] = value;
        ThemeManager.Initialize();
        MainWindow? window = null;
        try
        {
            window = new MainWindow { Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            var pane = (FrameworkElement)window.FindName("LeftPane");
            var active = ((ItemsControl)pane.FindName("TabStrip")).Items.OfType<SearchTab>().Single(t => t.IsActive);
            PumpUntil(() => active.CurrentFolder == fixture.Root && active.StatusKey != "Str_Status_Listing");
            Task command;
            if (parts[0] == "archive" || parts[0] == "copy-from-archive")
            {
                string virtualPath = fixture.Archive + "?";
                ((TextBox)pane.FindName("AddressBox")).Text = virtualPath;
                window.AddressBox_KeyDown(window, new KeyEventArgs(Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(window), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
                PumpUntil(() => active.CurrentFolder == virtualPath && active.StatusKey != "Str_Status_Listing");
            }
            if (parts[0] == "archive")
                command = window.ArchiveAdd(new[] { selected }, fixture.Archive, "");
            else if (parts[0].StartsWith("copy", StringComparison.Ordinal))
                command = window.DropOntoFolder(new[] { selected }, fixture.Target, DragDropEffects.Copy, true, false);
            else
            {
                active.Results.Clear();
                var row = new SearchResult { FileName = Path.GetFileName(selected), FilePath = selected,
                    Directory = Path.GetDirectoryName(selected)!, IsDirectory = true };
                active.Results.Add(row);
                ((ListBox)pane.FindName("ResultsList")).SelectedItem = row;
                window.MenuCompress_Click(window, new RoutedEventArgs());
                command = Task.CompletedTask;
            }
            PumpUntil(() => command.IsCompleted);
            command.GetAwaiter().GetResult();
            Assert.Contains("1 directory link(s) skipped.", active.StatusMessage);
            Assert.Contains("1 directory link(s) skipped.", ((TextBlock)window.FindName("StatusText")).Text);
            Assert.Equal("fixture", File.ReadAllText(fixture.File));
        }
        finally { window?.Close(); app.Shutdown(); }
    }

    private static void PumpUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "The command exceeded its deadline.");
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }
}

public static class FileArchiveDirectoryLinksScenario
{
    public static void Run(string argument)
    {
        string[] parts = argument.Split(':');
        bool rootLink = bool.Parse(parts[1]);
        using var fixture = new FileArchiveLinkFixture();
        string selected = rootLink ? fixture.Link : fixture.Source;
        if (parts[0] == "copy")
        {
            var copy = FileOps.CopyOrMove(new[] { selected }, fixture.Target, false,
                _ => ConflictChoice.Cancel, _ => { }, CancellationToken.None);
            Assert.Empty(copy.Failed);
            Assert.Equal(rootLink ? 0 : 1, copy.Succeeded);
            Assert.Equal(1, copy.Skipped);
            Assert.Equal(1, copy.SkippedDirectoryLinks);
            string[] files = Directory.GetFiles(fixture.Target, "*", SearchOption.AllDirectories);
            if (rootLink) Assert.Empty(files);
            else Assert.Equal("fixture", File.ReadAllText(Assert.Single(files)));
            Assert.Equal("fixture", File.ReadAllText(fixture.File));
            return;
        }
        var result = ArchiveWriter.Add(fixture.Archive, "", new[] { selected }, ArchiveCollision.Report,
            null, CancellationToken.None);
        Assert.Equal(1, result.SkippedDirectoryLinks);
        Assert.Empty(result.Rejected);
        using var zip = ZipFile.OpenRead(fixture.Archive);
        if (rootLink)
        {
            Assert.False(result.Ok);
            Assert.Equal("Str_Status_ArchiveNothingAdded", result.ErrorKey);
            Assert.Empty(zip.Entries);
        }
        else
        {
            Assert.True(result.Ok, result.ErrorDetail);
            var entry = Assert.Single(zip.Entries);
            Assert.Equal("source/fixture.txt", entry.FullName);
            using var reader = new StreamReader(entry.Open());
            Assert.Equal("fixture", reader.ReadToEnd());
        }
        Assert.Equal("fixture", File.ReadAllText(fixture.File));
    }
}

internal sealed class FileArchiveLinkFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "ks-file-links-" + Guid.NewGuid().ToString("N"));
    internal string Source => Path.Combine(Root, "source");
    internal string File => Path.Combine(Source, "fixture.txt");
    internal string Link => Path.Combine(Source, "ancestor");
    internal string Target => Path.Combine(Root, "target");
    internal string Archive => Path.Combine(Root, "result.zip");
    internal FileArchiveLinkFixture()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Target);
        System.IO.File.WriteAllText(File, "fixture");
        using (ZipFile.Open(Archive, ZipArchiveMode.Create)) { }
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + Link + "\" \"" + Source + "\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        Assert.True(process.WaitForExit(5000), "Owned junction creation timed out.");
        Assert.True(process.ExitCode == 0, process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd());
    }
    public void Dispose()
    {
        string boundary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(Root).StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        if (Directory.Exists(Link)) Directory.Delete(Link);
        Directory.Delete(Root, true);
    }
}
