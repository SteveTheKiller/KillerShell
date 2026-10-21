using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Shell;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

public sealed class TabCloseOwnershipTests
{
    [Fact]
    public Task DelayedCloseKeepsItsOwnerAndReleasesTheTerminal()
        => RuntimeComponentProcess.Run(nameof(TabCloseOwnershipScenario));
}

public static class TabCloseOwnershipScenario
{
    public static void Run()
    {
        using var fixture = new ShellWindowFixture();
        var window = fixture.Window;
        var left = (FilePane)window.FindName("LeftPane");
        var right = (FilePane)window.FindName("RightPane");
        ShellWindowFixture.Click(window, "DualPaneRailBtn");
        ShellWindowFixture.PumpUntil(() => right.Active != null && right.Active.IsBrowsing);
        window.FocusPane(left);
        var survivor = new SearchTab("survivor") { StatusMessage = "left survivor" };
        var terminal = new TerminalControl(TerminalSkin.Default);
        var victim = new SearchTab("closing terminal") { Term = terminal, Cts = new CancellationTokenSource() };
        left.Tabs.Add(survivor);
        left.Tabs.Add(victim);
        left.Active = victim;
        window.FocusPane(right);
        window.FocusPane(left);
        window.UpdateLayout();
        ShellWindowFixture.PumpUntil(() => left.TabStrip.ItemContainerGenerator.ContainerFromItem(victim) is FrameworkElement);
        string ready = Path.Combine(fixture.Root, "child.txt");
        terminal.Start("powershell.exe -NoProfile -NonInteractive -Command \"[IO.File]::WriteAllText('" +
            ready.Replace("'", "''") + "', [string]$PID); Start-Sleep -Seconds 30\"", fixture.Root);
        ShellWindowFixture.PumpUntil(() => File.Exists(ready));
        using var child = Process.GetProcessById(int.Parse(File.ReadAllText(ready)));
        int canceled = 0;
        using var registration = victim.Cts.Token.Register(() => canceled++);
        bool ghostShown = false;
        var source = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image));
        EventHandler changed = (_, _) => ghostShown |= left.TabFadeGhost.Source != null;
        source.AddValueChanged(left.TabFadeGhost, changed);
        var close = new Button { Tag = victim };
        window.CloseTab_Click(close, new RoutedEventArgs(Button.ClickEvent));
        Assert.Contains(victim, left.Tabs); // The loaded chip is still in its fade.
        window.CloseTab_Click(close, new RoutedEventArgs(Button.ClickEvent));
        var active = right.Active;
        int listingResets = 0;
        NotifyCollectionChangedEventHandler listingChanged = (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset) listingResets++;
        };
        active.Results.CollectionChanged += listingChanged;
        window.FocusPane(right);
        var groups = ((ItemsControl)window.FindName("TermsList")).ItemsSource;
        ShellWindowFixture.PumpUntil(() => !left.Tabs.Contains(victim));
        Assert.True(ghostShown, "The owner pane never displayed its close crossfade.");
        source.RemoveValueChanged(left.TabFadeGhost, changed);
        Assert.Same(right, window.Pane);
        Assert.Same(active, right.Active);
        Assert.Same(groups, ((ItemsControl)window.FindName("TermsList")).ItemsSource);
        Assert.Same(survivor, left.Active);
        Assert.Equal(1, canceled);
        Assert.Null(victim.Term);
        Assert.True(child.WaitForExit(3000), "Closing the owner tab left its real ConPTY child alive.");
        ShellWindowFixture.PumpUntil(() => left.TabFadeGhost.Source == null);
        Assert.Equal(Visibility.Collapsed, left.TabFadeGhost.Visibility);
        Assert.Null(right.TabFadeGhost.Source);
        string WatchDiagnostics() => "Listing resets=" + listingResets + "; focused=" + window.Pane.Name
            + "; status=" + active.StatusKey + "; text=" + active.StatusMessage;
        // The old rows can already contain the child file while activation is still listing.
        // Wait for this activation's publication before probing the installed watcher.
        ShellWindowFixture.PumpUntil(() => listingResets > 0, diagnostic: WatchDiagnostics);
        active.Results.CollectionChanged -= listingChanged;
        ShellWindowFixture.PumpUntil(() => active.Results.Any(r => r.FilePath == ready));
        string watched = Path.Combine(fixture.Root, "still-watched.txt");
        File.WriteAllText(watched, "fixture");
        ShellWindowFixture.PumpUntil(() => active.Results.Any(r => r.FilePath == watched), diagnostic: WatchDiagnostics);

        // A right pane can be hidden while its loaded tab chip is fading.
        window.FocusPane(right);
        var hidden = new SearchTab("hidden close");
        right.Tabs.Add(hidden);
        window.UpdateLayout();
        ShellWindowFixture.PumpUntil(() => right.TabStrip.ItemContainerGenerator.ContainerFromItem(hidden) is FrameworkElement);
        window.CloseTab_Click(new Button { Tag = hidden }, new RoutedEventArgs(Button.ClickEvent));
        ShellWindowFixture.Click(window, "DualPaneRailBtn");
        ShellWindowFixture.PumpUntil(() => !right.Tabs.Contains(hidden));
        Assert.Same(left, window.Pane);
        Assert.Same(active, right.Active);
    }
}

internal sealed class ShellWindowFixture : IDisposable
{
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly Application _app;
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "shell-tabs-" + Guid.NewGuid().ToString("N"));
    internal MainWindow Window { get; }

    internal ShellWindowFixture()
    {
        Directory.CreateDirectory(Root);
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        _app = RuntimeComponentProcess.StartApplication();
        var settings = new Dictionary<string, string>
        {
            ["HomeFolder"] = Root, ["EscQuit"] = "quit", ["RememberTabs"] = "0",
            ["CheckForUpdatesOnStartup"] = "0", ["CloseToTray"] = "0"
        };
        ThemeManager.GetSetting = key => settings.TryGetValue(key, out var value) ? value : null;
        ThemeManager.SetSetting = (key, value) => settings[key] = value;
        ThemeManager.Initialize();
        Window = new MainWindow { Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
        Window.Show();
        PumpUntil(() => ((FilePane)Window.FindName("LeftPane")).Active.IsBrowsing);
    }

    internal static void Click(MainWindow window, string name)
        => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    internal static void PumpUntil(Func<bool> done, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0,
        Func<string>? diagnostic = null)
    {
        var elapsed = Stopwatch.StartNew();
        do
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        } while (!done() && elapsed.ElapsedMilliseconds < 5000);
        Assert.True(done(), "The expected shell state at fixture line " + line + " did not arrive within five seconds. "
            + diagnostic?.Invoke());
    }

    public void Dispose()
    {
        bool closed = false;
        Window.Closed += (_, _) => closed = true;
        Window.Close();
        PumpUntil(() => closed);
        _app.Shutdown();
        SynchronizationContext.SetSynchronizationContext(_context);
        try { Directory.Delete(Root, true); } catch (IOException) { }
    }
}
