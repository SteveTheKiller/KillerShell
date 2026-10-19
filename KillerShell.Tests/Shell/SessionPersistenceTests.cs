using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Shell;

public sealed class SessionPersistenceTests
{
    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("hidden")]
    public Task AllPaneTabsSurviveCloseAndReopen(string scenario) => RunScenario(scenario);

    [Fact]
    public Task OptingOutClearsBothPaneSessions() => RunScenario("opt-out");

    [Fact]
    public Task LegacyRowsAndSavedDocumentsStillReopen() => RunScenario("legacy");

    [Fact]
    public Task SearchOptionsSurvivePaneFocusChangesAndReopen() => RunScenario("options");

    [Fact]
    public Task SearchOptionsSurviveOpeningSecondPane() => RunScenario("first-open");

    private static async Task RunScenario(string scenario)
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShell-session-test-" + Guid.NewGuid().ToString("N"));
        string assembly = typeof(SessionPersistenceTests).Assembly.Location.Replace("'", "''");
        string application = typeof(MainWindow).Assembly.Location.Replace("'", "''");
        string assertions = typeof(Assert).Assembly.Location.Replace("'", "''");
        string script = "$ErrorActionPreference = 'Stop'; try { " +
            "[Reflection.Assembly]::LoadFrom('" + application + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + assertions + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + assembly + "') | Out-Null; " +
            "[KillerShell.Tests.Shell.SessionPersistenceScenario]::Run('" + scenario + "', '" + root.Replace("'", "''") + "'); exit 0 " +
            "} catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -STA -EncodedCommand " +
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try
        {
            if (!process.WaitForExit(60000))
            {
                process.Kill();
                process.WaitForExit();
                Assert.Fail("The session-persistence scenario did not finish.");
            }
            Assert.True(process.ExitCode == 0, $"Scenario {scenario} exited with {process.ExitCode}.\n" + await output + await errors);
        }
        finally
        {
            // Process exit releases native directory handles before the fixture is removed.
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}

public static class SessionPersistenceScenario
{
    public static void Run(string scenario, string root)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        string[] folders = new[] { "left-one", "left-two", "right-one", "right-two" }
            .Select(name => Directory.CreateDirectory(Path.Combine(root, name)).FullName).ToArray();
        foreach (string folder in folders) File.WriteAllText(Path.Combine(folder, "fixture.txt"), "session fixture");
        var settings = new Dictionary<string, string>
        {
            ["EscQuit"] = "quit",
            ["SessionSchema"] = "2",
            ["HomeFolder"] = root,
            ["CheckForUpdatesOnStartup"] = "0",
            ["CloseToTray"] = "0"
        };
        ThemeManager.GetSetting = key => settings.TryGetValue(key, out string value) ? value : null;
        ThemeManager.SetSetting = (key, value) => settings[key] = value;
        Application.ResourceAssembly = typeof(MainWindow).Assembly;
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Exception? dispatcherFailure = null;
        app.DispatcherUnhandledException += (_, e) =>
        {
            dispatcherFailure ??= e.Exception;
            e.Handled = true;
        };
        foreach (string resource in new[] { "Themes/Dark.xaml", "Controls/Controls.xaml", "Controls/AppStyles.xaml", "Strings/en-US.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/KillerShell;component/" + resource)
            });
        ThemeManager.Initialize();
        try
        {
            if (scenario == "first-open")
            {
                var original = ShowWindow();
                SetSearchOptions(original, "*.before", "*.before-tmp", true);
                Click(original, "DualPaneRailBtn");
                FocusPane(original, "LeftPane");
                Pump(TimeSpan.FromMilliseconds(300));
                AssertSearchOptions(original, "*.before", "*.before-tmp", true);
                CloseWindow(original);
                var restored = ShowWindow();
                AssertSearchOptions(restored, "*.before", "*.before-tmp", true);
                CloseWindow(restored);
                return;
            }
            if (scenario == "legacy")
            {
                settings["Tabs"] = "Legacy|" + Uri.EscapeDataString(folders[0]) + "|*.txt|*.tmp|1|2|0|1~needle|";
                settings["TabsRight"] = "\u0001--edit \"" + Path.Combine(folders[2], "fixture.txt") + "\"";
                for (int i = 0; i < 2; i++)
                {
                    var restored = ShowWindow();
                    var tab = Assert.Single(Tabs(restored, "LeftPane"));
                    Assert.Equal("Legacy", tab.Title);
                    Assert.Equal(folders[0], tab.RootPath);
                    Assert.Equal("*.txt", tab.IncludePatterns);
                    Assert.Equal("*.tmp", tab.ExcludePatterns);
                    Assert.True(tab.CaseSensitive);
                    Assert.Equal(2, tab.SortIndex);
                    Assert.False(tab.SortAsc);
                    Assert.Equal("needle", Assert.Single(Assert.Single(tab.Groups).Terms).Pattern);
                    Assert.True(Assert.Single(Tabs(restored, "RightPane")).IsEditor);
                    Click(restored, "DualPaneRailBtn");
                    var editor = (TextEditor)((ContentControl)Pane(restored, "RightPane").FindName("EditorSlot")).Content;
                    Assert.Equal("session fixture", editor.Text);
                    Assert.False(editor.IsModified);
                    CloseWindow(restored);
                }
                return;
            }

            var window = ShowWindow();
            Navigate(window, "LeftPane", folders[0]);
            NewTab(window);
            Navigate(window, "LeftPane", folders[1]);
            Click(window, "DualPaneRailBtn");
            Pump(TimeSpan.FromMilliseconds(300));
            Navigate(window, "RightPane", folders[2]);
            NewTab(window);
            Navigate(window, "RightPane", folders[3]);
            if (scenario == "options")
            {
                SetSearchOptions(window, "*.right", "*.right-tmp", false);
                FocusPane(window, "LeftPane");
                SetSearchOptions(window, "*.left", "*.left-tmp", true);
                FocusPane(window, "RightPane");
                Click(window, "DualPaneRailBtn");
                Pump(TimeSpan.FromMilliseconds(300));
            }
            if (scenario == "left") FocusPane(window, "LeftPane");
            if (scenario == "hidden") Click(window, "DualPaneRailBtn");
            if (scenario == "opt-out") settings["RememberTabs"] = "0";
            CloseWindow(window);

            if (scenario == "opt-out") settings["RememberTabs"] = "1";
            var reopened = ShowWindow();
            if (scenario == "opt-out")
            {
                Assert.Equal(root, Assert.Single(Tabs(reopened, "LeftPane")).CurrentFolder);
                Assert.Empty(Tabs(reopened, "RightPane"));
                CloseWindow(reopened);
                return;
            }
            Assert.Equal(folders.Take(2), Tabs(reopened, "LeftPane").Select(tab => tab.CurrentFolder));
            Assert.Equal(folders.Skip(2), Tabs(reopened, "RightPane").Select(tab => tab.CurrentFolder));
            Assert.Equal(folders.Take(2), Tabs(reopened, "LeftPane").Select(tab => tab.RootPath));
            Assert.Equal(folders.Skip(2), Tabs(reopened, "RightPane").Select(tab => tab.RootPath));
            Assert.False(((FrameworkElement)reopened.FindName("RightPane")).IsVisible);
            if (scenario == "options")
            {
                Assert.Equal("*.left", Tabs(reopened, "LeftPane")[1].IncludePatterns);
                Assert.Equal("*.right", Tabs(reopened, "RightPane")[1].IncludePatterns);
                SelectTab(reopened, "LeftPane", 1);
                AssertSearchOptions(reopened, "*.left", "*.left-tmp", true);
            }
            Click(reopened, "DualPaneRailBtn");
            Pump(TimeSpan.FromMilliseconds(300));
            Assert.Equal(folders.Skip(2), Tabs(reopened, "RightPane").Select(tab => tab.CurrentFolder));
            Assert.Equal(folders[2], ((TextBlock)Pane(reopened, "RightPane").FindName("ScopePathLabel")).Text);
            if (scenario == "options")
            {
                SelectTab(reopened, "RightPane", 1);
                AssertSearchOptions(reopened, "*.right", "*.right-tmp", false);
                SelectTab(reopened, "RightPane", 0);
                AssertSearchOptions(reopened, "*.*", "", false);
                SelectTab(reopened, "RightPane", 1);
                AssertSearchOptions(reopened, "*.right", "*.right-tmp", false);
                FocusPane(reopened, "LeftPane");
                Pump(TimeSpan.FromMilliseconds(300));
                AssertSearchOptions(reopened, "*.left", "*.left-tmp", true);
            }
            CloseWindow(reopened);
        }
        finally
        {
            foreach (Window window in app.Windows.OfType<MainWindow>().ToArray()) window.Close();
            Pump(TimeSpan.FromMilliseconds(300));
            app.Shutdown();
            if (dispatcherFailure != null) throw new InvalidOperationException("The window dispatcher failed.", dispatcherFailure);
        }
    }

    private static MainWindow ShowWindow()
    {
        var window = new MainWindow { Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
        window.Show();
        Pump(TimeSpan.FromMilliseconds(300));
        return window;
    }

    private static void CloseWindow(MainWindow window)
    {
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Close();
        Pump(TimeSpan.FromMilliseconds(300));
        Assert.True(closed, "Closing must complete before the session is reopened.");
    }

    private static FrameworkElement Pane(MainWindow window, string paneName) => (FrameworkElement)window.FindName(paneName);

    private static SearchTab[] Tabs(MainWindow window, string paneName) =>
        ((ItemsControl)Pane(window, paneName).FindName("TabStrip")).Items.OfType<SearchTab>().ToArray();

    private static void FocusPane(MainWindow window, string paneName) =>
        Pane(window, paneName).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseDownEvent
        });

    private static void Navigate(MainWindow window, string paneName, string folder)
    {
        FocusPane(window, paneName);
        var address = (TextBox)Pane(window, paneName).FindName("AddressBox");
        address.Text = folder;
        address.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Enter)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        });
        Pump(TimeSpan.FromMilliseconds(300));
        Assert.Equal(folder, Tabs(window, paneName).Single(tab => tab.IsActive).CurrentFolder);
    }

    private static void NewTab(MainWindow window)
    {
        var button = Descendants(window).OfType<Button>().Single(candidate => Equals(candidate.ToolTip, window.FindResource("Str_TT_NewTab")));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(TimeSpan.FromMilliseconds(300));
    }

    private static void SetSearchOptions(MainWindow window, string include, string exclude, bool caseSensitive)
    {
        ((TextBox)window.FindName("IncludePatternsBox")).Text = include;
        ((TextBox)window.FindName("ExcludePatternsBox")).Text = exclude;
        ((CheckBox)window.FindName("CaseSensitiveCheck")).IsChecked = caseSensitive;
    }

    private static void AssertSearchOptions(MainWindow window, string include, string exclude, bool caseSensitive)
    {
        Assert.Equal(include, ((TextBox)window.FindName("IncludePatternsBox")).Text);
        Assert.Equal(exclude, ((TextBox)window.FindName("ExcludePatternsBox")).Text);
        Assert.Equal(caseSensitive, ((CheckBox)window.FindName("CaseSensitiveCheck")).IsChecked);
    }

    private static void SelectTab(MainWindow window, string paneName, int index)
    {
        FocusPane(window, paneName);
        var tab = Tabs(window, paneName)[index];
        var pane = Pane(window, paneName);
        // A narrow tab band exposes the overflow menu, whose click does not depend on mouse capture.
        ((FrameworkElement)pane.FindName("TabBar")).Width = 200;
        window.UpdateLayout();
        var button = (Button)pane.FindName("TabOverflowBtn");
        Assert.True(button.IsVisible);
        Assert.False(tab.IsStripVisible);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var menu = PresentationSource.CurrentSources.Cast<PresentationSource>()
            .Where(source => source.RootVisual != null)
            .SelectMany(source => Descendants(source.RootVisual)).OfType<ContextMenu>()
            .Single(candidate => ReferenceEquals(candidate.PlacementTarget, button));
        try
        {
            var item = menu.Items.OfType<MenuItem>().Single(candidate =>
                ((DockPanel)candidate.Header).Children.OfType<TextBlock>().Single().Text == tab.Title.Replace("_", "__"));
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }
        finally { menu.IsOpen = false; }
        Pump(TimeSpan.FromMilliseconds(300));
        Assert.True(tab.IsActive);
    }

    private static void Click(FrameworkElement root, string name) =>
        ((Button)root.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void Pump(TimeSpan duration)
    {
        if (Dispatcher.CurrentDispatcher.HasShutdownStarted) return;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
