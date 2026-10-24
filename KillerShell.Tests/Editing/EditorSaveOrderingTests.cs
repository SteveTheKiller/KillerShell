using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Shell;
using KillerShell.Tests.Terminal;
using Xunit;

namespace KillerShell.Tests.Editing;

[Trait("Category", "Window")]
public sealed class EditorSaveOrderingTests
{
    [Fact]
    public Task DelayedEarlierSaveCannotReplaceTheLatestSavedText()
        => EditorScenarioProcess.Run(nameof(EditorSaveOrderingScenario));

    [Fact]
    public Task NormalSaveWaitsForThePendingElevatedSave()
        => EditorScenarioProcess.Run(nameof(EditorSaveOrderingScenario), "command");

    [Theory]
    [InlineData("canceled")]
    [InlineData("launch-failed")]
    [InlineData("helper-failed")]
    [InlineData("path-changed")]
    [InlineData("newer-edit")]
    [InlineData("closed")]
    [InlineData("focus-changed")]
    public Task PendingSaveRespectsFailurePathAndEditorLifetime(string scenario)
        => EditorScenarioProcess.Run(nameof(EditorSaveOrderingScenario), scenario);
}

public static class EditorSaveOrderingScenario
{
    public static void Run() => Run("elevated");

    public static void Run(string scenario)
    {
        string root = Path.Combine(Path.GetTempPath(), "ks-editor-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
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
        var children = new List<Process>();
        try
        {
            window = new MainWindow { Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
            window.Show();
            Pump();
            string path = Path.Combine(root, "original.txt");
            File.WriteAllText(path, "original");
            window.OpenForEditing(path);
            Pump();
            var pane = (FrameworkElement)window.FindName("LeftPane");
            var tabs = (ItemsControl)pane.FindName("TabStrip");
            var owner = tabs.Items.OfType<SearchTab>().Single(tab => tab.IsEditor);
            var editor = owner.Editor!;
            string release = Path.Combine(root, "release-first");
            int launches = 0;
            Process? Start(ProcessStartInfo requested)
            {
                string[] arguments = Regex.Matches(requested.Arguments, "\"([^\"]*)\"|(\\S+)")
                    .Cast<Match>().Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToArray();
                int command = Array.IndexOf(arguments, "--elevated-save");
                Assert.True(command >= 0);
                string gate = ++launches == 1 ? release : "";
                if (scenario == "helper-failed" && launches == 2)
                {
                    Assert.Equal("Str_Ed_SaveFailed", owner.StatusKey);
                    Assert.True(editor.Dirty);
                    Directory.Delete(path);
                }
                string Q(string value) => "'" + value.Replace("'", "''") + "'";
                string script = "$ErrorActionPreference='Stop'; try { " +
                    "[Reflection.Assembly]::LoadFrom(" + Q(typeof(MainWindow).Assembly.Location) + ") | Out-Null; " +
                    "[Reflection.Assembly]::LoadFrom(" + Q(typeof(EditorSaveOrderingScenario).Assembly.Location) + ") | Out-Null; " +
                    "[KillerShell.Tests.Editing.EditorSaveOrderingScenario]::Publish(" +
                    string.Join(",", arguments.Skip(command + 1).Take(4)
                        .Concat(new[] { gate, Path.Combine(root, "done-" + launches) }).Select(Q)) + "); exit 0 " +
                    "} catch { [Console]::Error.WriteLine($_); exit 1 }";
                var child = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe", UseShellExecute = false, CreateNoWindow = true,
                    Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script))
                })!;
                children.Add(Process.GetProcessById(child.Id));
                if (scenario == "newer-edit" && launches == 2) editor.Text = "later edit";
                return child;
            }

            editor.Text = "save A";
            if (scenario == "canceled" || scenario == "launch-failed")
            {
                window.RetrySaveElevated(owner, "original.txt", _ =>
                    throw new System.ComponentModel.Win32Exception(scenario == "canceled" ? 1223 : 2));
                Assert.Equal("original", File.ReadAllText(path));
                Assert.True(editor.Dirty);
                window.EditorSave_Click(window, new RoutedEventArgs());
                Assert.Equal("save A", File.ReadAllText(path));
                Assert.False(editor.Dirty);
                return;
            }
            window.RetrySaveElevated(owner, "original.txt", Start);
            if (scenario == "helper-failed")
            {
                File.Delete(path);
                Directory.CreateDirectory(path);
            }
            editor.Text = "save B";
            string finalPath = path;
            if (scenario == "path-changed")
            {
                finalPath = Path.Combine(root, "save-as.txt");
                editor.AdoptPath(finalPath, editor.CurrentEncoding);
            }
            KillerShell.Editing.EditorControl? otherEditor = null;
            bool normalSave = scenario == "command" || scenario == "focus-changed";
            if (normalSave)
            {
                window.EditorSave_Click(window, new RoutedEventArgs());
                Assert.Equal("original", File.ReadAllText(path));
                Assert.True(editor.Dirty);
                if (scenario == "focus-changed")
                {
                    string otherPath = Path.Combine(root, "other.txt");
                    File.WriteAllText(otherPath, "other original");
                    window.OpenForEditing(otherPath);
                    Pump();
                    otherEditor = tabs.Items.OfType<SearchTab>().Single(tab => tab.IsActive).Editor!;
                    otherEditor.Text = "other unsaved edit";
                }
            }
            else
            {
                window.RetrySaveElevated(owner, "original.txt", Start);
                PumpUntil(() => launches == 1 || File.ReadAllText(path) == "save B");
            }
            if (scenario == "closed")
            {
                editor.CompleteSave(editor.CurrentEncoding, editor.Text);
                window.CloseTab_Click(new Button { Tag = owner }, new RoutedEventArgs());
                PumpUntil(() => owner.Editor == null);
                Assert.Null(owner.Editor);
                File.WriteAllText(release, "go");
                PumpUntil(() => children.All(child => child.HasExited));
                for (int i = 0; i < 12; i++) Pump();
                Assert.Equal(1, launches);
                Assert.Equal("save A", File.ReadAllText(path));
                return;
            }
            File.WriteAllText(release, "go");
            PumpUntil(() => File.Exists(Path.Combine(root, "done-1"))
                && (normalSave ? !editor.Dirty : File.Exists(Path.Combine(root, "done-2"))));
            Assert.Equal("save B", File.ReadAllText(finalPath));
            if (scenario == "path-changed") Assert.Equal("save A", File.ReadAllText(path));
            if (scenario == "newer-edit")
            {
                PumpUntil(() => children.All(child => child.HasExited));
                Pump();
                Assert.Equal("later edit", editor.Text);
                Assert.True(editor.Dirty);
                return;
            }
            PumpUntil(() => !editor.Dirty);
            Assert.False(editor.Dirty);
            if (otherEditor != null)
            {
                Assert.Equal("other original", File.ReadAllText(otherEditor.FilePath));
                Assert.Equal("other unsaved edit", otherEditor.Text);
                Assert.True(otherEditor.Dirty);
            }
        }
        finally
        {
            foreach (var child in children)
                try { if (!child.HasExited) child.Kill(); child.Dispose(); } catch (InvalidOperationException) { }
            if (window != null)
            {
                var pane = (FrameworkElement)window.FindName("LeftPane");
                foreach (var tab in ((ItemsControl)pane.FindName("TabStrip")).Items.OfType<SearchTab>())
                    tab.Editor?.CompleteSave(tab.Editor.CurrentEncoding, tab.Editor.Text);
                window.Close();
            }
            app.Shutdown();
            Directory.Delete(root, true);
        }
    }

    public static void Publish(string staged, string target, string hash, string response, string gate, string completed)
    {
        var watch = Stopwatch.StartNew();
        while (gate.Length > 0 && !File.Exists(gate))
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("The first helper was not released.");
            System.Threading.Thread.Sleep(20);
        }
        bool copied = VerifiedFile.TryCopy(staged, hash, target, out string error);
        File.WriteAllText(completed, "done");
        ElevatedSaveResponse.Send(response, error);
        if (!copied) throw new IOException(error);
    }

    private static void PumpUntil(Func<bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), "The editor save did not finish within its deadline.");
            Pump();
        }
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
