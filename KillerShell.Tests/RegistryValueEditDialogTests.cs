using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using KillerShell.Shell;
using Microsoft.Win32;
using Xunit;

namespace KillerShell.Tests
{
    /// <summary>
    /// Drives the real registry value dialog through its base radios, text box and Save button.
    /// Switching between hexadecimal and decimal must only change how the number is written,
    /// never the number that Save returns.
    /// </summary>
    public sealed class RegistryValueEditDialogTests
    {
        [Theory]
        [InlineData("dword-hex-to-decimal")]
        [InlineData("dword-repeated-switches")]
        [InlineData("dword-zero")]
        [InlineData("dword-max")]
        [InlineData("dword-hex-letters")]
        [InlineData("qword-max")]
        [InlineData("qword-negative")]
        [InlineData("edited-decimal-to-hex")]
        [InlineData("invalid-text-keeps-base")]
        [InlineData("empty-text-switches")]
        [InlineData("cancel")]
        public async Task SwitchingBaseNeverChangesTheSavedValue(string scenario)
        {
            // Each WPF Application needs its own process, including when other UI tests run first.
            string assembly = typeof(RegistryValueEditDialogTests).Assembly.Location.Replace("'", "''");
            string application = typeof(MainWindow).Assembly.Location.Replace("'", "''");
            string assertions = typeof(Assert).Assembly.Location.Replace("'", "''");
            string script = "$ErrorActionPreference = 'Stop'; try { " +
                "[Reflection.Assembly]::LoadFrom('" + application + "') | Out-Null; " +
                "[Reflection.Assembly]::LoadFrom('" + assertions + "') | Out-Null; " +
                "[Reflection.Assembly]::LoadFrom('" + assembly + "') | Out-Null; " +
                "[KillerShell.Tests.RegistryValueEditScenario]::Run('" + scenario + "'); exit 0 " +
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
                Assert.Fail("The registry dialog scenario did not finish.");
            }
            Assert.True(process.ExitCode == 0, $"Scenario {scenario} exited with {process.ExitCode}.\n" + await output + await errors);
        }
    }

    public static class RegistryValueEditScenario
    {
        // Called by the isolated STA process through its public test entry point.
        public static void Run(string scenario)
        {
            Application.ResourceAssembly = typeof(MainWindow).Assembly;
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string resource in new[] { "Themes/Dark.xaml", "Controls/Controls.xaml", "Controls/AppStyles.xaml", "Strings/en-US.xaml" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/KillerShell;component/" + resource)
                });
            try { Check(scenario); }
            finally { app.Shutdown(); }
        }

        private static void Check(string scenario)
        {
            switch (scenario)
            {
                case "dword-hex-to-decimal":
                {
                    var d = Open(RegistryValueKind.DWord, 16);
                    Assert.Equal("10", d.Text);
                    d.Decimal();
                    Assert.Equal("16", d.Text);
                    Assert.Equal(16, d.Save());
                    break;
                }
                case "dword-repeated-switches":
                {
                    var d = Open(RegistryValueKind.DWord, 0x1234);
                    for (int i = 0; i < 4; i++) { d.Decimal(); Assert.Equal("4660", d.Text); d.Hex(); Assert.Equal("1234", d.Text); }
                    d.Decimal();
                    Assert.Equal(0x1234, d.Save());
                    break;
                }
                case "dword-zero":
                {
                    var d = Open(RegistryValueKind.DWord, 0);
                    d.Decimal(); d.Hex(); d.Decimal();
                    Assert.Equal("0", d.Text);
                    Assert.Equal(0, d.Save());
                    break;
                }
                case "dword-max":
                {
                    var d = Open(RegistryValueKind.DWord, unchecked((int)0xFFFFFFFF));
                    Assert.Equal("ffffffff", d.Text);
                    d.Decimal();
                    Assert.Equal("4294967295", d.Text);
                    Assert.Equal(-1, d.Save());
                    break;
                }
                case "dword-hex-letters":
                {
                    var d = Open(RegistryValueKind.DWord, 0xABCDEF);
                    d.Decimal();
                    Assert.Equal("11259375", d.Text);
                    d.Hex();
                    Assert.Equal("abcdef", d.Text);
                    Assert.Equal(0xABCDEF, d.Save());
                    break;
                }
                case "qword-max":
                {
                    var d = Open(RegistryValueKind.QWord, -1L);
                    Assert.Equal("ffffffffffffffff", d.Text);
                    d.Decimal();
                    Assert.Equal("18446744073709551615", d.Text);
                    d.Hex();
                    Assert.Equal(-1L, d.Save());
                    break;
                }
                case "qword-negative":
                {
                    var d = Open(RegistryValueKind.QWord, long.MinValue);
                    d.Decimal();
                    Assert.Equal("9223372036854775808", d.Text);
                    Assert.Equal(long.MinValue, d.Save());
                    break;
                }
                case "edited-decimal-to-hex":
                {
                    var d = Open(RegistryValueKind.DWord, 1);
                    d.Decimal();
                    d.Text = "20";
                    d.Hex();
                    Assert.Equal("14", d.Text);
                    Assert.Equal(20, d.Save());
                    break;
                }
                case "invalid-text-keeps-base":
                {
                    // "ff" is not a decimal number. Switching to hexadecimal must not quietly
                    // reread it as 255: the base stays decimal and Save reports the bad number.
                    var d = Open(RegistryValueKind.DWord, 1);
                    d.Decimal();
                    d.Text = "ff";
                    d.Hex();
                    Assert.True(d.IsDecimal, "The base switched even though the text could not be converted.");
                    Assert.Equal("ff", d.Text);
                    Assert.True(d.ErrorShown, "No error explained why the base did not change.");
                    Assert.Null(d.SaveExpectingFailure());
                    break;
                }
                case "empty-text-switches":
                {
                    var d = Open(RegistryValueKind.DWord, 1);
                    d.Text = "";
                    d.Decimal();
                    Assert.True(d.IsDecimal);
                    d.Text = "255";
                    d.Hex();
                    Assert.Equal("ff", d.Text);
                    Assert.Equal(255, d.Save());
                    break;
                }
                case "cancel":
                {
                    var d = Open(RegistryValueKind.DWord, 16);
                    d.Decimal();
                    d.Cancel();
                    Assert.False(d.Dialog.Confirmed);
                    Assert.Null(d.Dialog.ResultValue);
                    break;
                }
                default:
                    throw new ArgumentException("Unknown scenario " + scenario);
            }
        }

        private static Driver Open(RegistryValueKind kind, object value) =>
            new(new RegistryValueEditDialog("Fixture", kind, value));

        private sealed class Driver
        {
            public RegistryValueEditDialog Dialog { get; }
            public Driver(RegistryValueEditDialog dialog) => Dialog = dialog;

            private T Find<T>(string name) where T : class => (T)Dialog.FindName(name);
            private TextBox Box => Find<TextBox>("NumberBox");

            public string Text { get => Box.Text; set => Box.Text = value; }
            public bool IsDecimal => Find<RadioButton>("DecRadio").IsChecked == true;
            public bool ErrorShown => Find<TextBlock>("ErrorText").Visibility == Visibility.Visible;

            public void Decimal() => Find<RadioButton>("DecRadio").IsChecked = true;
            public void Hex() => Find<RadioButton>("HexRadio").IsChecked = true;

            public object? Save()
            {
                Click("OkBtn");
                Assert.True(Dialog.Confirmed, "Save did not accept the value: " + Find<TextBlock>("ErrorText").Text);
                return Dialog.ResultValue;
            }

            public object? SaveExpectingFailure()
            {
                Click("OkBtn");
                Assert.False(Dialog.Confirmed, "Save accepted a value the visible base cannot read.");
                return Dialog.ResultValue;
            }

            public void Cancel()
            {
                foreach (var button in FindButtons(Dialog))
                    if (Equals(button.Content, Application.Current.FindResource("Str_Btn_Cancel")))
                    {
                        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                        return;
                    }
                Assert.Fail("Cancel button not found.");
            }

            private void Click(string name) =>
                Find<Button>(name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            private static System.Collections.Generic.IEnumerable<Button> FindButtons(DependencyObject root)
            {
                foreach (object child in LogicalTreeHelper.GetChildren(root))
                {
                    if (child is not DependencyObject node) continue;
                    if (node is Button button) yield return button;
                    foreach (var nested in FindButtons(node)) yield return nested;
                }
            }
        }
    }
}
