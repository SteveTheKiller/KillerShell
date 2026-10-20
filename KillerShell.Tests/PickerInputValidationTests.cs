using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests;

public sealed class PickerInputValidationTests
{
    [Fact]
    public async Task MalformedNamesStayInThePickerThroughButtonAndEnter()
    {
        string assembly = typeof(PickerInputValidationTests).Assembly.Location.Replace("'", "''");
        string application = typeof(FileDialog).Assembly.Location.Replace("'", "''");
        string assertions = typeof(Assert).Assembly.Location.Replace("'", "''");
        string script = "$ErrorActionPreference = 'Stop'; try { " +
            "[Reflection.Assembly]::LoadFrom('" + application + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + assertions + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + assembly + "') | Out-Null; " +
            "[KillerShell.Tests.PickerInputValidationScenario]::Run(); exit 0 " +
            "} catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -STA -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000)) { process.Kill(); Assert.Fail("Picker validation did not finish."); }
        Assert.True(process.ExitCode == 0, await output + await errors);
    }
}

public static class PickerInputValidationScenario
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var read = ThemeManager.GetSetting;
            var write = ThemeManager.SetSetting;
            Application? app = null;
            try
            {
                ThemeManager.GetSetting = _ => null;
                ThemeManager.SetSetting = (_, _) => { };
                Application.ResourceAssembly = typeof(FileDialog).Assembly;
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (string resource in new[] { "Themes/Dark.xaml", "Controls/Controls.xaml", "Controls/AppStyles.xaml", "Strings/en-US.xaml" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/KillerShell;component/" + resource)
                    });
                foreach (var mode in new[] { FileDialogMode.Open, FileDialogMode.Save })
                foreach (bool enter in new[] { false, true })
                foreach (string text in new[] { "bad|name.txt", "bad\"name.txt", "bad:name.txt", new string('a', 300) + ".txt" })
                {
                    var picker = new FileDialog(mode);
                    var name = (TextBox)picker.FindName("FileNameBox");
                    name.Text = text;
                    if (enter)
                        name.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, new TestPresentationSource(), 0, Key.Enter)
                        { RoutedEvent = Keyboard.KeyDownEvent });
                    else
                        ((Button)picker.FindName("AcceptButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.Equal(text, name.Text);
                    Assert.Null(picker.DialogResult);
                    Assert.Equal("invalid characters", ((TextBlock)picker.FindName("SelMeta")).Text);
                    picker.Close();
                }
            }
            catch (Exception error) { failure = error; }
            finally
            {
                app?.Shutdown();
                ThemeManager.GetSetting = read;
                ThemeManager.SetSetting = write;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Picker input validation timed out.");
        if (failure != null) throw failure;
    }

    private sealed class TestPresentationSource : PresentationSource
    {
        public override System.Windows.Media.Visual RootVisual { get; set; } = null!;
        public override bool IsDisposed => false;
        protected override System.Windows.Media.CompositionTarget GetCompositionTargetCore() => null!;
    }
}
