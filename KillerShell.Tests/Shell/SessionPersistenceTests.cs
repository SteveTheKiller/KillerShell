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

    private static async Task RunScenario(string scenario)
    {
        string assembly = typeof(SessionPersistenceTests).Assembly.Location.Replace("'", "''");
        string application = typeof(MainWindow).Assembly.Location.Replace("'", "''");
        string assertions = typeof(Assert).Assembly.Location.Replace("'", "''");
        string script = "$ErrorActionPreference = 'Stop'; try { " +
            "[Reflection.Assembly]::LoadFrom('" + application + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + assertions + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + assembly + "') | Out-Null; " +
            "[KillerShell.Tests.Shell.SessionPersistenceScenario]::Run('" + scenario + "'); exit 0 " +
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
        if (!process.WaitForExit(60000))
        {
            process.Kill();
            Assert.Fail("The session-persistence scenario did not finish.");
        }
        Assert.True(process.ExitCode == 0, $"Scenario {scenario} exited with {process.ExitCode}.\n" + await output + await errors);
    }
}

public static class SessionPersistenceScenario
{
    public static void Run(string scenario)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        string root = Path.Combine(Path.GetTempPath(), "KillerShell-session-test-" + Guid.NewGuid().ToString("N"));
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
            Click(reopened, "DualPaneRailBtn");
            Pump(TimeSpan.FromMilliseconds(300));
            Assert.Equal(folders.Skip(2), Tabs(reopened, "RightPane").Select(tab => tab.CurrentFolder));
            Assert.Equal(folders[2], ((TextBlock)Pane(reopened, "RightPane").FindName("ScopePathLabel")).Text);
            CloseWindow(reopened);
        }
        finally
        {
            foreach (Window window in app.Windows.OfType<MainWindow>().ToArray()) window.Close();
            Pump(TimeSpan.FromMilliseconds(300));
            app.Shutdown();
            Directory.Delete(root, true);
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
