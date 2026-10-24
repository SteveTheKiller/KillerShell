using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Shell;
using KillerShell.Tests.Editing;
using KillerShell.Tests.Terminal;
using Xunit;

namespace KillerShell.Tests.Shell;

[Trait("Category", "Window")]
public sealed class SearchCompletionTests
{
    [Theory]
    [InlineData("unreadable")]
    [InlineData("links")]
    public Task IncompleteOrSkippedPathsShowPartialResultsInTheLoadedWindow(string mode)
        => EditorScenarioProcess.Run(typeof(SearchCompletionScenario).FullName!, mode);
}

public static class SearchCompletionScenario
{
    public static void Run(string mode)
    {
        using var fixture = new KillerShell.Tests.Services.SearchLinkFixture();
        string root = fixture.Root;
        string path = fixture.File;
        File.WriteAllText(path, "needle");
        if (mode == "links") fixture.AddLink();
        Environment.SetEnvironmentVariable("KS_STATE", Path.Combine(root, "state.json"));
        var app = RuntimeComponentProcess.StartApplication();
        var settings = new Dictionary<string, string>
        {
            ["EscQuit"] = "quit", ["RememberTabs"] = "0", ["HomeFolder"] = root,
            ["CheckForUpdatesOnStartup"] = "0", ["CloseToTray"] = "0"
        };
        ThemeManager.GetSetting = key => settings.TryGetValue(key, out string value) ? value : null;
        ThemeManager.SetSetting = (key, value) => settings[key] = value;
        ThemeManager.Initialize();
        MainWindow? window = null;
        Exception? asynchronousFailure = null;
        EventHandler<FirstChanceExceptionEventArgs> firstChance = (_, e) =>
        {
            if (e.Exception is InvalidOperationException
                && (e.Exception.StackTrace ?? string.Empty).IndexOf(
                    "System.Windows.Threading.Dispatcher.VerifyAccess", StringComparison.Ordinal) >= 0
                && Interlocked.CompareExchange(ref asynchronousFailure, e.Exception, null) == null)
                Console.Error.WriteLine("Search fixture dispatcher access failure: " + e.Exception);
        };
        DispatcherUnhandledExceptionEventHandler unhandled = (_, e) =>
        {
            Interlocked.CompareExchange(ref asynchronousFailure, e.Exception, null);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.FirstChanceException += firstChance;
        app.DispatcherUnhandledException += unhandled;
        void CheckAsynchronousFailure()
        {
            Exception? failure = Volatile.Read(ref asynchronousFailure);
            if (failure != null)
                throw new InvalidOperationException("The search fixture observed an asynchronous UI failure.", failure);
        }
        var originalContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        try
        {
            window = new MainWindow { Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            Console.WriteLine("Loaded search window.");
            var pane = (FrameworkElement)window.FindName("LeftPane");
            var active = ((ItemsControl)pane.FindName("TabStrip")).Items.OfType<SearchTab>().Single(t => t.IsActive);
            var opening = Stopwatch.StartNew();
            while (active.StatusKey != "Str_Status_Listed" && opening.Elapsed < TimeSpan.FromSeconds(10)) Pump();
            CheckAsynchronousFailure();
            Assert.Equal("Str_Status_Listed", active.StatusKey);
            active.PipeFiles = mode == "unreadable" ? [path] : null;
            active.IsBrowsing = false;
            active.Groups.Clear();
            var group = new TermGroup();
            group.Terms.Add(new SearchTerm { Mode = SearchTerm.SearchMode.Content, Pattern = "needle" });
            active.Groups.Add(group);
            using var locked = mode == "unreadable"
                ? new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
            // Search checks its folders off the UI thread first, so it starts a moment after the
            // click; wait for it to start and then to finish.
            bool started = false;
            System.ComponentModel.PropertyChangedEventHandler searching = (_, e) =>
            {
                if (e.PropertyName == nameof(SearchTab.IsSearching) && active.IsSearching) started = true;
            };
            active.PropertyChanged += searching;
            ((Button)window.FindName("SearchButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Console.WriteLine("Started locked-file content search.");
            var watch = Stopwatch.StartNew();
            while ((!started || active.IsSearching) && watch.Elapsed < TimeSpan.FromSeconds(10))
            {
                Pump();
                CheckAsynchronousFailure();
            }
            Pump();
            CheckAsynchronousFailure();
            Assert.False(active.IsSearching);
            Assert.Equal(app.FindResource("Str_Btn_Search"), ((Button)window.FindName("SearchButton")).Content);
            Assert.Equal(Visibility.Collapsed, ((TextBlock)pane.FindName("ScannedText")).Visibility);
            Console.WriteLine("Search completion: " + active.StatusKey + ": " + active.StatusMessage);
            Assert.Equal("Str_Status_SearchPartial", active.StatusKey);
            Assert.Equal(mode == "unreadable" ? 0 : 1, active.Results.Count);
            Assert.Equal(mode == "unreadable" ? 1 : 0, active.Engine.IncompletePaths);
            Assert.Equal(mode == "links" ? 1 : 0, active.Engine.SkippedDirectoryLinks);
            Assert.Contains("1", active.StatusMessage);
            Assert.Equal("WarnBrush", MainWindow.StatusBrushKey(active.StatusKey, false));
        }
        finally
        {
            try
            {
                Console.WriteLine("Closing search fixture.");
                window?.Close(); app.Shutdown();
                Console.WriteLine("Search fixture cleaned.");
            }
            finally
            {
                AppDomain.CurrentDomain.FirstChanceException -= firstChance;
                app.DispatcherUnhandledException -= unhandled;
                SynchronizationContext.SetSynchronizationContext(originalContext);
            }
            CheckAsynchronousFailure();
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
