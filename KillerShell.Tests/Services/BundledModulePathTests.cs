using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

// KillerShell's own KillerScripts and KillerPivot load first in every terminal tab, ahead of any
// copy installed from PSGallery, so a fix that ships with KillerShell is the one that runs.
public sealed class BundledModulePathTests
{
    private const string Root = @"C:\Users\x\AppData\Local\KillerShell\Modules\1.5.2.0";
    private const string User = @"C:\Users\x\Documents\PowerShell\Modules";
    private const string System = @"C:\Program Files\PowerShell\Modules";

    [Fact]
    public void TheBundledFolderGoesFirst()
        => Assert.Equal(Root + ";" + User + ";" + System, BundledModulePath.WithBundledFirst(User + ";" + System, Root));

    [Fact]
    public void AnEmptyPathIsJustTheBundledFolder()
        => Assert.Equal(Root, BundledModulePath.WithBundledFirst("", Root));

    [Fact]
    public void AnEarlierPositionIsMovedToTheFrontNotRepeated()
        => Assert.Equal(Root + ";" + User + ";" + System,
            BundledModulePath.WithBundledFirst(User + ";" + Root.ToUpperInvariant() + "\\;" + System, Root));

    [Fact]
    public void OlderKillerShellModuleFoldersAreDropped()
        => Assert.Equal(Root + ";" + User,
            BundledModulePath.WithBundledFirst(@"C:\Users\x\AppData\Local\KillerShell\Modules\1.5.1.0;" + User, Root));

    [Fact]
    public void EmptyEntriesAreDropped()
        => Assert.Equal(Root + ";" + User + ";" + System, BundledModulePath.WithBundledFirst(";" + User + ";;" + System + ";", Root));

    // The terminal startup command runs once per shell, after $PROFILE and again in every nested
    // pwsh, so it must leave exactly one bundled entry however many times it runs.
    [Theory]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    public void RunningTheStartupSnippetTwiceLeavesOneBundledEntry(string host)
    {
        string snippet = BundledModulePath.PowerShellSnippet(Root);
        string command = "$env:PSModulePath = '" + User + ";" + System + "'; " + snippet + snippet +
            "($env:PSModulePath -split ';') -join '|'";
        var start = new global::System.Diagnostics.ProcessStartInfo
        {
            FileName = host,
            Arguments = "-NoProfile -Command \"" + command + "\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        using var process = global::System.Diagnostics.Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        Assert.Equal(Root + "|" + User + "|" + System, output);
    }
}
