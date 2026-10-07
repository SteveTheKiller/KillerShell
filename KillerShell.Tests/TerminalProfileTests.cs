using System;
using System.Linq;
using System.Text;
using KillerShell.Services;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests;

public sealed class TerminalProfileTests
{
    [Theory]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\steve\My Project", "Ubuntu", "/home/steve/My Project")]
    [InlineData(@"\\wsl$\Ubuntu-24.04\", "Ubuntu-24.04", "/")]
    [InlineData(@"\\WSL.LOCALHOST\Debian", "Debian", "/")]
    public void WslPathsPreserveDistributionAndLinuxCase(string path, string distro, string linux)
    {
        Assert.True(WslDistributions.TryParsePath(path, out string actualDistro, out string actualLinux));
        Assert.Equal(distro, actualDistro);
        Assert.Equal(linux, actualLinux);
        Assert.Equal(linux, WslDistributions.ToLinuxPath(path, distro));
        Assert.True(WslDistributions.PathsEqual(path, WslDistributions.ToWindowsPath(distro, linux)));
    }

    [Theory]
    [InlineData(@"C:\My Project", "/mnt/c/My Project")]
    [InlineData("/home/steve/Project", "/home/steve/Project")]
    [InlineData(@"\\wsl$\Debian\home\a", "~")]
    [InlineData(@"\\server\share", "~")]
    [InlineData(null, "~")]
    public void WslStartPathsMapWindowsDrivesAndAvoidOtherDistributions(string? path, string expected)
        => Assert.Equal(expected, WslDistributions.ToLinuxPath(path, "Ubuntu"));

    [Fact]
    public void LinuxFolderMembershipIsCaseSensitive()
    {
        Assert.False(WslDistributions.PathsEqual(@"\\wsl$\Ubuntu\home\Project", @"\\wsl.localhost\Ubuntu\home\project"));
        Assert.True(WslDistributions.PathsEqual(@"C:\Project", @"c:\project"));
    }

    [Fact]
    public void CustomProfileAndElevationSurviveHandoffWithoutSettings()
    {
        var entry = new TerminalProfileEntry { Name = "Custom PowerShell", Executable = @"C:\My Tools\pwsh.exe", Arguments = "-NoLogo -NoProfile",
            StartFolder = @"D:\My Projects", Kind = TerminalShellKind.Custom, CustomKind = TerminalShellKind.PowerShell, Elevated = true, Hidden = true };
        var copy = Assert.Single(TerminalProfileStore.Deserialize(TerminalProfileStore.Serialize([entry])));
        Assert.Equal(entry.Arguments, copy.Arguments);
        Assert.True(copy.Hidden);
        var restored = TerminalProfile.FromHandoffToken(copy.Resolve().HandoffToken());
        Assert.NotNull(restored);
        Assert.Equal(TerminalShellKind.PowerShell, restored!.Kind);
        Assert.Equal(entry.Name, restored.Name);
        Assert.Equal(entry.StartFolder, restored.StartFolder);
        Assert.True(restored.Elevated);
        Assert.Equal("\"C:\\My Tools\\pwsh.exe\" -NoLogo -NoProfile", restored.CommandLine);
        Assert.DoesNotContain("KillerPrompt", restored.CommandLine);
    }

    [Fact]
    public void WslLaunchLoadsBashStartupBeforeEditablePromptAndReportsCwd()
    {
        var profile = TerminalProfile.Wsl("Ubuntu-24.04");
        string command = profile.LaunchCommand("/home/steve/My Project");
        Assert.Contains("--distribution Ubuntu-24.04 --cd \"/home/steve/My Project\" --exec bash", command);
        Assert.DoesNotContain("PowerShell", command);
        var encoded = System.Text.RegularExpressions.Regex.Match(command, "printf %s ([A-Za-z0-9+/=]+)").Groups[1].Value;
        string script = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        Assert.Contains(". ~/.bashrc", script);
        Assert.Contains("KS_DISTRO='Ubuntu-24.04'", script);
        Assert.Contains("KillerPrompt.bash", script);
        Assert.True(script.IndexOf(". ~/.bashrc", StringComparison.Ordinal) < script.IndexOf("KillerPrompt.bash", StringComparison.Ordinal));
        Assert.Contains("PROMPT_COMMAND+=", script);
        Assert.DoesNotContain("PS1=", script);
        Assert.Equal("clear\r", profile.ClearCommand);
        Assert.Equal(@"\\wsl.localhost\Ubuntu-24.04\home\steve", profile.BrowsePath("/home/steve"));
        var restored = TerminalProfile.FromHandoffToken(profile.HandoffToken());
        Assert.Equal("Ubuntu-24.04", restored!.Distribution);
    }

    [Fact]
    public void ShellCommandsQuoteLiteralPaths()
    {
        var wsl = TerminalProfile.Wsl("Ubuntu");
        Assert.Equal("cd -- '/home/a'\"'\"'b/$project'\r", wsl.ChangeDirectoryCommand("/home/a'b/$project"));
        Assert.Equal("cd ~\r", wsl.ChangeDirectoryCommand("~"));
        var ps = new TerminalProfileEntry { Kind = TerminalShellKind.Custom, CustomKind = TerminalShellKind.PowerShell, Executable = "pwsh.exe" }.Resolve();
        Assert.Equal("Set-Location -LiteralPath 'C:\\a''b\\$project'\r", ps.ChangeDirectoryCommand(@"C:\a'b\$project"));
        Assert.Equal("cd /d \"D:\\Projects\"\r", TerminalProfile.Cmd().ChangeDirectoryCommand(@"D:\Projects"));
    }

    [Fact]
    public void InvalidSavedDataAndHandoffFailSafely()
    {
        Assert.Empty(TerminalProfileStore.Deserialize("<broken"));
        Assert.Null(TerminalProfile.FromHandoffToken("not base64"));
        Assert.False(WslDistributions.TryParsePath(@"\\wsl$\", out _, out _));
        Assert.Throws<ArgumentException>(() => TerminalProfile.Wsl("Ubuntu\";bad"));
    }

    [Theory]
    [InlineData("Ubuntu extra")]
    [InlineData("Ubuntu\t--exec")]
    [InlineData("Ubuntu\r\n--exec")]
    public void WslDistributionCannotInjectAnotherLaunchArgument(string name)
    {
        Assert.False(WslDistributions.ValidName(name));
        Assert.Throws<ArgumentException>(() => TerminalProfile.Wsl(name));
    }

    [Theory]
    [InlineData("abc", "\"abc\"")]
    [InlineData("C:\\", "\"C:\\\\\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    public void WindowsArgumentQuotingPreservesQuotesAndTrailingSlashes(string argument, string expected)
        => Assert.Equal(expected, WslDistributions.QuoteArgument(argument));

    [Fact]
    public void LinuxCwdReportFlowsThroughTerminalParser()
    {
        var buffer = new TerminalBuffer(80, 25);
        var parser = new VtParser(buffer);
        string? cwd = null;
        buffer.DirectoryChanged += directory => cwd = directory;
        byte[] data = Encoding.UTF8.GetBytes("\u001b]9;9;/home/steve/My Project\u0007");
        parser.Feed(data, data.Length);
        Assert.Equal("/home/steve/My Project", cwd);
    }

    [Fact]
    public void ChoosingProfileDoesNotAlsoLaunchDefaultThroughParentMenu()
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                var terminal = new TerminalControl(TerminalSkin.Lcd) { ShellKind = TerminalShellKind.Wsl };
                int defaultLaunches = 0;
                terminal.MenuCommand += _ => defaultLaunches++;
                var menu = (System.Windows.Controls.ContextMenu)typeof(TerminalControl)
                    .GetMethod("BuildMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(terminal, null)!;
                var parent = menu.Items.OfType<System.Windows.Controls.MenuItem>().Single(item => item.InputGestureText == "F8");
                var child = new System.Windows.Controls.MenuItem { Header = "Ubuntu" };
                parent.Items.Add(child);
                int selectedLaunches = 0;
                child.Click += (_, _) => selectedLaunches++;
                child.RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                Assert.Equal(1, selectedLaunches);
                Assert.Equal(0, defaultLaunches);
                var editProfile = menu.Items.OfType<System.Windows.Controls.MenuItem>().Single(item => item.InputGestureText == "Ctrl+,");
                Assert.False(editProfile.IsEnabled);
                Assert.True(menu.Items.OfType<System.Windows.Controls.MenuItem>().Single(item => item.InputGestureText == "Ctrl+Shift+E").IsEnabled);
                Assert.True(menu.Items.OfType<System.Windows.Controls.MenuItem>().Single(item => item.InputGestureText == "Ctrl+Shift+R").IsEnabled);
                Assert.False(menu.Items.OfType<System.Windows.Controls.MenuItem>().Single(item => item.InputGestureText == "Ctrl+Shift+Q").IsEnabled);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
