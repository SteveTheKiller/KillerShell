using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using KillerShell.Services;
using KillerShell.Shell;
using KillerShell.Tools;
using Xunit;

namespace KillerShell.Tests.Tools;

public sealed class StorageAccessibilityTests
{
    [Theory]
    [InlineData("keyboard")]
    [InlineData("automation")]
    [InlineData("scan-shortcut")]
    public async Task StorageNodesExposeKeyboardAndAutomationSelection(string scenario)
    {
        string script = "$ErrorActionPreference='Stop'; try { " +
            "[Reflection.Assembly]::LoadFrom('" + typeof(MainWindow).Assembly.Location.Replace("'", "''") + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + typeof(Assert).Assembly.Location.Replace("'", "''") + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + typeof(StorageAccessibilityTests).Assembly.Location.Replace("'", "''") + "') | Out-Null; " +
            "[KillerShell.Tests.Tools.StorageAccessibilityScenario]::Run('" + scenario + "'); exit 0 } catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -STA -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(45000)) { process.Kill(); Assert.Fail("Storage accessibility fixture timed out."); }
        Assert.True(process.ExitCode == 0, await output + await errors);
    }
}

public static class StorageAccessibilityScenario
{
    public static void Run(string scenario)
    {
        ThemeManager.GetSetting = _ => null;
        ThemeManager.SetSetting = (_, _) => { };
        Application.ResourceAssembly = typeof(MainWindow).Assembly;
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (string resource in new[] { "Themes/Dark.xaml", "Controls/Controls.xaml", "Controls/AppStyles.xaml", "Strings/en-US.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/KillerShell;component/" + resource) });
        ThemeManager.Initialize();
        string root = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", "storage", Guid.NewGuid().ToString("N"));
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
        string folder = Path.Combine(root, "big");
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, "large.bin"), new byte[11]);
        File.WriteAllBytes(Path.Combine(folder, "small.bin"), new byte[7]);
        File.WriteAllBytes(Path.Combine(root, "other.bin"), new byte[5]);
        var statuses = new List<string>();
        ModifierKeys modifiers = ModifierKeys.None;
        int scanStarts = 0;
        using var readStarted = new ManualResetEventSlim();
        var control = new StorageAnalyzerControl(root, (path, token) =>
        {
            if (path == root) { Interlocked.Increment(ref scanStarts); readStarted.Set(); }
            return StorageDirectoryReader.Read(path, token);
        }, canUseMft: _ => false, keyboardModifiers: () => modifiers) { ReportStatus = statuses.Add };
        var window = new Window { Content = control, Width = 900, Height = 600, Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
        try
        {
            window.Show();
            if (scenario == "scan-shortcut")
            {
                var target = Descendants(control).OfType<TextBox>().Single();
                target.Focus();
                Assert.Same(target, Keyboard.FocusedElement);
                modifiers = ModifierKeys.Control;
                SendKey(target, Key.Return);
                SendKey(target, Key.Return);
                Assert.True(readStarted.Wait(TimeSpan.FromSeconds(3)), "Ctrl+Enter in the focused target must start the real scan.");
                PumpUntil(() => control.CreateReport()?.TotalSize == 23);
                Assert.Equal(1, Volatile.Read(ref scanStarts));
                modifiers = ModifierKeys.None;
                foreach (var key in new[] { Key.D, Key.M, Key.C })
                {
                    var input = SendKey(target, key);
                    Assert.False(input.Handled);
                }
                target.CaretIndex = target.Text.Length;
                TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, target, "dmc"));
                Assert.EndsWith("dmc", target.Text);
                Assert.Equal(1, Volatile.Read(ref scanStarts));
                return;
            }
            control.ScanFolder(root);
            PumpUntil(() => control.CreateReport()?.TotalSize == 23);
            if (scenario == "keyboard")
            {
                control.Focus();
                SendKey(control, Key.Down);
                Assert.Equal(folder, statuses.Last());
                SendKey(control, Key.Right);
                Assert.Equal(folder, control.CreateReport()!.ViewRoot);
                Assert.Equal(Path.Combine(folder, "large.bin"), statuses.Last());
                SendKey(control, Key.Down);
                Assert.Equal(Path.Combine(folder, "small.bin"), statuses.Last());
                SendKey(control, Key.Left);
                Assert.Equal(folder, statuses.Last());
                SendKey(control, Key.Back);
                Assert.Equal(root, control.CreateReport()!.ViewRoot);
                return;
            }
            var tree = Descendants(control).OfType<UIElement>()
                .Select(UIElementAutomationPeer.CreatePeerForElement)
                .FirstOrDefault(peer => peer?.GetAutomationControlType() == AutomationControlType.Tree);
            Assert.NotNull(tree);
            var selection = Assert.IsAssignableFrom<ISelectionProvider>(tree!.GetPattern(PatternInterface.Selection));
            Assert.Empty(selection.GetSelection());
            var rootPeer = Assert.Single(tree.GetChildren());
            var big = rootPeer.GetChildren().Single(peer => peer.GetName().StartsWith("big", StringComparison.Ordinal));
            Assert.Contains("18", big.GetName());
            Assert.Contains(folder, big.GetHelpText());
            var bigSelection = Assert.IsAssignableFrom<ISelectionItemProvider>(big.GetPattern(PatternInterface.SelectionItem));
            bigSelection.Select();
            Assert.True(bigSelection.IsSelected);
            Assert.Single(selection.GetSelection());
            var expand = Assert.IsAssignableFrom<IExpandCollapseProvider>(big.GetPattern(PatternInterface.ExpandCollapse));
            Assert.Equal(ExpandCollapseState.Collapsed, expand.ExpandCollapseState);
            Assert.Empty(big.GetChildren());
            expand.Expand();
            Pump(TimeSpan.FromMilliseconds(50));
            Assert.Equal(folder, control.CreateReport()!.ViewRoot);
            Assert.Equal(ExpandCollapseState.Expanded, expand.ExpandCollapseState);
            Assert.Equal(2, big.GetChildren().Count);
            control.Focus();
            SendKey(control, Key.Down);
            Assert.Equal(Path.Combine(root, "other.bin"), statuses.Last());
            Assert.Equal(root, control.CreateReport()!.ViewRoot);
            bigSelection.Select();
            expand.Expand();
            window.Width = 750;
            Pump(TimeSpan.FromMilliseconds(50));
            Assert.True(bigSelection.IsSelected);
            expand.Collapse();
            Assert.Equal(root, control.CreateReport()!.ViewRoot);
            big.SetFocus();
            Assert.True(big.HasKeyboardFocus());
            var targetInput = Descendants(control).OfType<TextBox>().Single();
            targetInput.Focus();
            Assert.Same(targetInput, Keyboard.FocusedElement);
            Assert.False(big.HasKeyboardFocus());
            control.ScanFolder(root);
            PumpUntil(() => control.CreateReport()?.TotalSize == 23);
            Assert.Empty(selection.GetSelection());
            Assert.Throws<ElementNotAvailableException>(() => bigSelection.Select());
        }
        finally
        {
            control.Shutdown(); window.Close(); app.Shutdown();
            string ownedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "KillerShell.Tests", "storage")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(ownedRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture escaped scratch root.");
            Directory.Delete(root, true);
        }
    }

    private static KeyEventArgs SendKey(UIElement element, Key key)
    {
        var input = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(element), Environment.TickCount, key)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        element.RaiseEvent(input);
        return input;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static void PumpUntil(Func<bool> complete)
    {
        var watch = Stopwatch.StartNew();
        while (!complete() && watch.Elapsed < TimeSpan.FromSeconds(10)) Pump(TimeSpan.FromMilliseconds(20));
        Assert.True(complete(), "Storage scan did not finish.");
        Pump(TimeSpan.FromMilliseconds(50));
    }

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
}
