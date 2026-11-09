using System.Collections.Generic;
using System.Threading.Tasks;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Shell;

// Copy SHA-256 reports to the tab that asked, start to finish, even when the hash finishes after
// the user switched tabs.
public sealed class HashCopyTests
{
    [Fact]
    public async Task BothStatusesGoToTheTabThatAsked()
    {
        var asked = new List<string>();
        var release = new TaskCompletionSource<string?>();
        string? copied = null;

        var run = HashCopy.Run(@"C:\big\disk.iso", _ => release.Task, (key, _) => asked.Add(key), text => { copied = text; return true; });
        Assert.Equal(new[] { "Str_Status_Hashing" }, asked);

        release.SetResult("ABC123");
        await run;

        Assert.Equal(new[] { "Str_Status_Hashing", "Str_Status_HashCopied" }, asked);
        Assert.Equal("ABC123", copied);
    }

    [Fact]
    public async Task AnUnreadableFileReportsTheFailure()
    {
        var asked = new List<string>();
        await HashCopy.Run(@"C:\locked.bin", _ => Task.FromResult<string?>(null), (key, _) => asked.Add(key), _ => true);
        Assert.Equal(new[] { "Str_Status_Hashing", "Str_Status_HashFailed" }, asked);
    }

    [Fact]
    public async Task ABusyClipboardIsReported()
    {
        var asked = new List<string>();
        await HashCopy.Run(@"C:\a.bin", _ => Task.FromResult<string?>("FF"), (key, _) => asked.Add(key), _ => false);
        Assert.Equal(new[] { "Str_Status_Hashing", "Str_Status_ClipboardBusy" }, asked);
    }
}
