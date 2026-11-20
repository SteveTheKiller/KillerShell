using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Terminal;

[Collection(NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class ResultMenuOpenSpeedTests
{
    [Fact]
    public Task TheFileCardOpensQuicklyOnceWarm()
        => RuntimeComponentProcess.Run(nameof(ResultMenuOpenSpeedScenario), 120000);
}

public static class ResultMenuOpenSpeedScenario
{
    // A right-click card must feel instant. The first opening pays for template creation; every
    // opening after that is measured from the key press until the card has rendered.
    private const double BudgetMs = 250;

    public static void Run()
    {
        var settings = new Dictionary<string, string>
        {
            ["Theme"] = "Decay", ["AppScale"] = "1.1", ["DarkAccent"] = "Red",
            ["DetailsPaneOpenL"] = "1", ["DetailsPanePositionL"] = "right", ["DetailsPaneWidthL"] = "247",
            ["BookmarksOpen"] = "1",
        };
        using var fixture = new ShellWindowFixture(settings);
        var pane = (FilePane)fixture.Window.FindName("LeftPane");
        string[] kinds = [".txt", ".pdf", ".png", ".exe", ".dll", ".zip", ".ps1", ".docx"];
        for (int i = 0; i < 400; i++)
            File.WriteAllText(Path.Combine(fixture.Root, $"file{i:000}{kinds[i % kinds.Length]}"), new string('x', i * 37));
        // Real files too: actual executables, libraries and wallpapers, so icons, version info and
        // previews have something genuine to read.
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        foreach (string name in new[] { "notepad.exe", "shell32.dll", "kernel32.dll", "mspaint.exe", "imageres.dll" })
            if (File.Exists(Path.Combine(system, name))) File.Copy(Path.Combine(system, name), Path.Combine(fixture.Root, "real-" + name));
        foreach (string image in Directory.EnumerateFiles(@"C:\Windows\Web", "*.jpg", SearchOption.AllDirectories).Take(4))
            File.Copy(image, Path.Combine(fixture.Root, "real-" + Path.GetFileName(image)), true);
        ShellWindowFixture.PumpUntil(() => pane.Active.Results.Count(row => row.FileName.StartsWith("real-")) >= 5);
        var reals = pane.Active.Results.Where(row => row.FileName.StartsWith("real-")).ToList();

        double OpenOnce(Models.SearchResult row)
        {
            // Pointing at an unselected row moves the selection there as part of the click, so the
            // selection change is inside the measurement, as it is for a real right-click.
            var clock = Stopwatch.StartNew();
            pane.ResultsList.SelectedItems.Clear();
            pane.ResultsList.SelectedItem = row;
            FileCommandCard.Open(pane);
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Render);
            clock.Stop();
            pane.ResultsList.ContextMenu.IsOpen = false;
            ShellWindowFixture.PumpUntil(() => !pane.ResultsList.ContextMenu.IsOpen);
            return clock.Elapsed.TotalMilliseconds;
        }

        double first = OpenOnce(pane.Active.Results[3]);
        var warm = reals.Concat(pane.Active.Results.Skip(20).Take(3)).Select(OpenOnce).ToList();
        double median = warm.OrderBy(ms => ms).ElementAt(warm.Count / 2);
        Console.WriteLine($"[KSPERF] first={first:F0}ms warm={string.Join(",", warm.Select(ms => ms.ToString("F0")))} median={median:F0}ms");
        Assert.True(median < BudgetMs, $"Warm card open took {median:F0} ms (first {first:F0} ms); the budget is {BudgetMs} ms.");
    }
}
