using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using KillerShell.Services;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Shell;

public sealed class WindowCloseTests
{
    [Theory]
    [InlineData("window")]
    [InlineData("tray")]
    [InlineData("two-panes")]
    [InlineData("hidden-pane")]
    [InlineData("discard")]
    [InlineData("clean")]
    [InlineData("generic")]
    [InlineData("generic-cancel")]
    [InlineData("session-ending")]
    public async Task ClosingRespectsUnsavedDocuments(string scenario)
    {
        // Each WPF Application needs its own process, including when other UI tests run first.
        string assembly = typeof(WindowCloseTests).Assembly.Location.Replace("'", "''");
        string application = typeof(MainWindow).Assembly.Location.Replace("'", "''");
        string assertions = typeof(Assert).Assembly.Location.Replace("'", "''");
        string script = "$ErrorActionPreference = 'Stop'; try { " +
            "[Reflection.Assembly]::LoadFrom('" + application + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + assertions + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + assembly + "') | Out-Null; " +
            "[KillerShell.Tests.Shell.WindowCloseScenario]::Run('" + scenario + "'); exit 0 " +
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
            Assert.Fail("The window-close scenario did not finish.");
        }
        Assert.True(process.ExitCode == 0, $"Scenario {scenario} exited with {process.ExitCode}.\n" + await output + await errors);
    }
}

public static class WindowCloseScenario
{
    // Called by the isolated STA process through its public test entry point.
    public static void Run(string scenario)
    {
        // Routed input normally runs under this context, including asynchronous pane navigation.
        var originalContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        bool trayExit = scenario == "tray";
        bool twoPanes = scenario == "two-panes" || scenario == "hidden-pane";
        bool acceptClose = scenario == "discard" || scenario == "clean";
        var settings = new Dictionary<string, string>
        {
            ["EscQuit"] = "quit",
            ["RememberTabs"] = "0",
            ["CheckForUpdatesOnStartup"] = "0",
            ["CloseToTray"] = trayExit ? "1" : "0"
        };
        if (scenario.StartsWith("generic", StringComparison.Ordinal)) settings.Remove("EscQuit");
        ThemeManager.GetSetting = key => settings.TryGetValue(key, out string value) ? value : null;
        ThemeManager.SetSetting = (key, value) => settings[key] = value;
        Application.ResourceAssembly = typeof(MainWindow).Assembly;
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var app = new SessionApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
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
        MainWindow? window = null;
        var editors = new List<TextEditor>();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        int discardPrompts = 0;
        bool closed = false;
        bool blockedEditsDuringFade = false;
        bool finishing = false;
        timer.Tick += (_, _) =>
        {
            foreach (ConfirmDialog dialog in app.Windows.OfType<ConfirmDialog>().ToArray())
            {
                dialog.Opacity = 0;
                bool discard = ((TextBlock)dialog.FindName("MsgText")).Text == (string)app.FindResource("Str_Dlg_DiscardMsg");
                if (discard) discardPrompts++;
                bool accept = finishing || acceptClose || (twoPanes && discardPrompts == 1)
                    || (!discard && scenario != "generic-cancel");
                Click(dialog, accept ? "OkBtn" : "CancelBtn");
            }
        };
        try
        {
            window = new MainWindow { Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
            window.Closed += (_, _) => closed = true;
            window.Closing += (_, _) => blockedEditsDuringFade |= !window.IsEnabled;
            window.Show();
            Pump(TimeSpan.FromMilliseconds(200));
            editors.Add(NewDocument(window, "LeftPane"));
            if (twoPanes)
            {
                Click(window, "DualPaneRailBtn");
                editors.Add(NewDocument(window, "RightPane"));
                if (scenario == "hidden-pane") Click(window, "DualPaneRailBtn");
                Pump(TimeSpan.FromMilliseconds(500));
            }
            if (scenario == "clean") editors[0].IsModified = false;
            timer.Start();
            if (scenario == "session-ending")
                Assert.True(app.EndSession().Cancel, "Cancel must veto session ending before shutdown starts.");
            else
                window.Close();
            Pump(TimeSpan.FromMilliseconds(500));
            if (trayExit)
            {
                Assert.Equal(0, discardPrompts);
                Assert.False(window.IsVisible);
                TrayAction("Str_Tray_Exit");
                Pump(TimeSpan.FromMilliseconds(500));
            }
            Assert.Equal(scenario == "clean" || scenario == "generic-cancel" ? 0 : twoPanes ? 2 : 1, discardPrompts);
            if (acceptClose)
            {
                Assert.True(blockedEditsDuringFade, "Approved close must block typing during the fade.");
                Assert.True(closed, "Accepting discard or closing a clean editor must finish closing.");
                return;
            }
            Assert.False(closed);
            Assert.True(window.IsEnabled, "A canceled close must leave the window editable.");
            foreach (var editor in editors)
            {
                Assert.Equal("unsaved close regression", editor.Text);
                Assert.True(editor.IsModified);
            }
            Assert.False(settings.ContainsKey("Tabs"), "Cancel must not persist an exit session.");
            if (trayExit)
            {
                TrayAction("Str_Tray_Open");
                Assert.True(window.IsVisible);
                window.Close();
                Assert.False(window.IsVisible, "A canceled tray exit must still allow hiding.");
            }
            if (twoPanes)
            {
                window.Close();
                Pump(TimeSpan.FromMilliseconds(300));
                Assert.Equal(3, discardPrompts);
                Assert.False(closed, "Canceling a later document must not approve a future close.");
            }
        }
        finally
        {
            finishing = true;
            settings["CloseToTray"] = "0";
            foreach (var editor in editors) editor.IsModified = false;
            if (window != null && !closed) window.Close();
            Pump(TimeSpan.FromMilliseconds(300));
            timer.Stop();
            app.Shutdown();
            SynchronizationContext.SetSynchronizationContext(originalContext);
            if (dispatcherFailure != null) throw new InvalidOperationException("The window dispatcher failed.", dispatcherFailure);
        }
    }

    private static void Click(FrameworkElement root, string name) =>
        ((Button)root.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static TextEditor NewDocument(MainWindow window, string paneName)
    {
        Click(window, "NewDocRailBtn");
        var pane = (FrameworkElement)window.FindName(paneName);
        var editor = (TextEditor)((ContentControl)pane.FindName("EditorSlot")).Content;
        editor.Text = "unsaved close regression";
        Assert.True(editor.IsModified);
        return editor;
    }

    private sealed class SessionApplication : Application
    {
        public SessionEndingCancelEventArgs EndSession()
        {
            // Windows normally supplies these args; create the OS boundary without signing out.
            var args = (SessionEndingCancelEventArgs)Activator.CreateInstance(typeof(SessionEndingCancelEventArgs),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null, new object[] { ReasonSessionEnding.Logoff }, null)!;
            OnSessionEnding(args);
            return args;
        }
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

    private static void TrayAction(string resource)
    {
        var tray = PresentationSource.CurrentSources.OfType<HwndSource>().Single(source =>
        {
            var title = new StringBuilder(256);
            GetWindowText(source.Handle, title, title.Capacity);
            return title.ToString() == AppInfo.DisplayName + "TrayIcon";
        });
        // Deliver the notification area's right-click callback to this process's own tray window.
        SendMessage(tray.Handle, 0x8051, IntPtr.Zero, new IntPtr(0x0205));
        string header = string.Format((string)Application.Current.FindResource(resource), AppInfo.DisplayName);
        var menu = PresentationSource.CurrentSources.OfType<HwndSource>()
            .SelectMany(source => Descendants(source.RootVisual)).OfType<ContextMenu>()
            .Single(candidate => candidate.IsOpen);
        var item = menu.Items.OfType<MenuItem>().Single(candidate => Equals(candidate.Header, header));
        menu.IsOpen = false;
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject? root)
    {
        if (root == null) yield break;
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
