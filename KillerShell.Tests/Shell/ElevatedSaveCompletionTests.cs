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
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using KillerShell.Models;
using KillerShell.Services;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Shell;

[Trait("Category", "Window")]
public sealed class ElevatedSaveCompletionTests
{
    [Theory]
    [InlineData("closed-success")]
    [InlineData("closed-failure")]
    [InlineData("unchanged")]
    [InlineData("text-changed")]
    [InlineData("text-undo-baseline")]
    [InlineData("encoding-changed")]
    [InlineData("path-changed")]
    [InlineData("replacement-editor")]
    public async Task CompletionRespectsEditorLifetimeAndStagedSnapshot(string scenario)
    {
        string assembly = typeof(ElevatedSaveCompletionTests).Assembly.Location.Replace("'", "''");
        string product = typeof(MainWindow).Assembly.Location.Replace("'", "''");
        string assertions = typeof(Assert).Assembly.Location.Replace("'", "''");
        string script = "$ErrorActionPreference='Stop';try { " +
            "[Reflection.Assembly]::LoadFrom('" + product + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + assertions + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + assembly + "') | Out-Null; " +
            "[KillerShell.Tests.Shell.ElevatedSaveCompletionScenario]::Run('" + scenario + "');exit 0 " +
            "} catch { [Console]::Error.WriteLine($_.Exception.ToString());exit 1 }";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe", UseShellExecute = false, CreateNoWindow = true,
            Arguments = "-NoProfile -NonInteractive -STA -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(); Assert.Fail("The isolated save-completion scenario timed out."); }
        Assert.True(process.ExitCode == 0, scenario + ": " + await output + await errors);
    }
}

public static class ElevatedSaveCompletionScenario
{
    public static void Run(string scenario)
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        string root = Path.Combine(Path.GetTempPath(), "KillerShell-save-completion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? oldState = Environment.GetEnvironmentVariable("KS_STATE");
        Environment.SetEnvironmentVariable("KS_STATE", Path.Combine(root, "state.json"));
        var settings = new Dictionary<string, string> { ["EscQuit"] = "quit", ["RememberTabs"] = "0", ["HomeFolder"] = root, ["CheckForUpdatesOnStartup"] = "0", ["CloseToTray"] = "0" };
        ThemeManager.GetSetting = key => settings.TryGetValue(key, out string value) ? value : null;
        ThemeManager.SetSetting = (key, value) => settings[key] = value;
        Application.ResourceAssembly = typeof(MainWindow).Assembly;
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string resource in new[] { "Themes/Dark.xaml", "Controls/Controls.xaml", "Controls/AppStyles.xaml", "Strings/en-US.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/KillerShell;component/" + resource) });
        ThemeManager.Initialize();
        MainWindow? window = null;
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
            editor.Document.Replace(0, editor.Document.TextLength, "staged");
            editor.SetEncoding(new UTF8Encoding(true));
            string stagedText = editor.Text, stagedPath = editor.FilePath;
            Encoding stagedEncoding = editor.CurrentEncoding;
            if (scenario.StartsWith("closed", StringComparison.Ordinal))
            {
                // Close through the real tab command without a modal discard prompt.
                editor.CompleteSave(editor.CurrentEncoding, editor.Text);
                window.CloseTab_Click(new Button { Tag = owner }, new RoutedEventArgs());
                Pump();
                Assert.Null(owner.Editor);
                Assert.DoesNotContain(owner, tabs.Items.OfType<SearchTab>());
                editor.IsModified = true;
            }
            else if (scenario == "text-changed" || scenario == "text-undo-baseline")
                editor.Document.Replace(0, editor.Document.TextLength, "newer edit");
            else if (scenario == "encoding-changed") editor.SetEncoding(new UnicodeEncoding(false, true));
            else if (scenario == "path-changed") editor.AdoptPath(Path.Combine(root, "renamed.txt"), new UTF8Encoding(false));
            else if (scenario == "replacement-editor")
            {
                owner.Editor = new KillerShell.Editing.EditorControl(string.Empty);
                owner.Editor.LoadEmpty();
                owner.Editor.Text = "replacement editor";
            }
            string? previousStatus = owner.StatusKey;
            string diagnostic = "Complete original saved to: C:\\recovery\\original.original";
            window.CompleteElevatedSave(owner, editor, "original.txt", stagedText, stagedEncoding, stagedPath,
                scenario == "closed-failure" ? 1 : 0, diagnostic);
            if (scenario == "replacement-editor")
            {
                Assert.True(owner.Editor!.IsModified);
                Assert.True(editor.IsModified);
                Assert.Equal(previousStatus, owner.StatusKey);
            }
            else if (scenario.StartsWith("closed", StringComparison.Ordinal))
            {
                Assert.True(editor.IsModified, "A completed save changed the detached editor.");
                Assert.Equal(previousStatus, owner.StatusKey);
                if (scenario == "closed-failure")
                {
                    var active = tabs.Items.OfType<SearchTab>().Single(tab => tab.IsActive);
                    Assert.Equal("Str_Ed_SaveFailed", active.StatusKey);
                    Assert.Contains(diagnostic, active.StatusArgs!);
                    Assert.Contains(diagnostic, ((TextBlock)window.FindName("StatusText")).Text);
                }
            }
            else Assert.Equal(scenario != "unchanged", editor.Dirty);
            if (scenario == "unchanged")
            {
                editor.SetEncoding(new UTF8Encoding(false));
                Assert.True(editor.Dirty);
                editor.SetEncoding(new UTF8Encoding(true));
                Assert.False(editor.Dirty);
            }
            if (scenario == "encoding-changed")
            {
                editor.SetEncoding(new UTF8Encoding(true));
                Assert.False(editor.Dirty);
            }
            if (scenario == "text-undo-baseline")
            {
                editor.Undo();
                Assert.Equal("staged", editor.Text);
                Assert.False(editor.Dirty, "The text written by the helper must be clean.");
                editor.Undo();
                Assert.Equal("original", editor.Text);
                Assert.True(editor.Dirty, "Undo to the old disk text must stay dirty after helper save.");
                editor.Redo();
                Assert.Equal("staged", editor.Text);
                Assert.False(editor.Dirty);
            }
        }
        finally
        {
            if (window != null)
            {
                var pane = (FrameworkElement)window.FindName("LeftPane");
                foreach (var tab in ((ItemsControl)pane.FindName("TabStrip")).Items.OfType<SearchTab>())
                    if (tab.Editor != null) tab.Editor.CompleteSave(tab.Editor.CurrentEncoding, tab.Editor.Text);
                window.Close();
                Pump();
            }
            app.Shutdown();
            Environment.SetEnvironmentVariable("KS_STATE", oldState);
            Directory.Delete(root, true);
        }
    }

    private static void Pump()
    {
        if (Dispatcher.CurrentDispatcher.HasShutdownStarted) return;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
