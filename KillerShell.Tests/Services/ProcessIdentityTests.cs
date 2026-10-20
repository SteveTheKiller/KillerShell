using System;
using System.Diagnostics;
using System.Threading.Tasks;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class ProcessIdentityTests
{
    [Fact]
    public async Task UnverifiableOriginalCannotLaunchAReplacement()
    {
        bool launched = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProcessRestart.RestartAsync(
            42, new ProcessStartInfo("fixture"), open: _ => null, start: _ => launched = true));
        Assert.False(launched);
    }

    [Theory]
    [InlineData(null, 123, false)]
    [InlineData(123L, 456L, false)]
    [InlineData(123L, 123L, true)]
    public async Task RecycledMissingAndExitedInstancesAreNeverActedOn(long? sampled, long held, bool exited)
    {
        var process = new ControlledInstance { StartTimeUtcTicks = held, HasExited = exited };
        bool launched = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProcessRestart.RestartAsync(
            42, new ProcessStartInfo("fixture"), open: _ => process, start: _ => launched = true,
            expectedStartTimeUtcTicks: sampled));
        Assert.False(process.Killed);
        Assert.False(launched);
        Assert.True(process.Disposed);
    }

    [Fact]
    public void PinRejectsWrongOrUnknownSampleIdentityAndRetainsExactInstance()
    {
        using var child = Process.Start(new ProcessStartInfo("ping.exe", "-n 30 127.0.0.1")
        { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            long created = child.StartTime.ToUniversalTime().Ticks;
            Assert.Null(ProcessListLogic.PinProcess(child.Id, created + 1));
            Assert.Null(ProcessListLogic.PinProcess(child.Id, null));
            Assert.False(child.HasExited);
            using var pinned = ProcessListLogic.PinProcess(child.Id, created);
            Assert.NotNull(pinned);
            child.Kill();
            Assert.True(child.WaitForExit(5000));
            Assert.True(pinned!.HasExited);
            Assert.Equal(created, pinned.StartTime.ToUniversalTime().Ticks);
        }
        finally
        {
            if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); }
        }
    }

    [Theory]
    [InlineData(638400000000000009L, 638400000000000000L, true)]
    [InlineData(638400000000000010L, 638400000000000000L, false)]
    [InlineData(null, 638400000000000000L, false)]
    [InlineData(638400000000000000L, null, false)]
    public void RestartMetadataMustBelongToTheSampledCreation(long? sampled, long? metadata, bool match)
        => Assert.Equal(match, ProcessListLogic.MatchesCreationTime(sampled, metadata));

    [Theory]
    [InlineData(123L, 456L, "replacement.exe")]
    [InlineData(null, 123L, "replacement.exe")]
    [InlineData(123L, null, "replacement.exe")]
    [InlineData(123L, 123L, "")]
    public void UncorrelatedDisplayMetadataCannotAuthorizeProcessActions(long? held, long? metadata, string name)
        => Assert.Null(ProcessListLogic.VerifySampleMetadata(held, metadata, name, "--replacement", "replacement.exe", "1"));

    [Fact]
    public void VerifiedSampleUsesTheCorrelatedNameInsteadOfACachedEnumerationName()
    {
        var sample = ProcessListLogic.VerifySampleMetadata(123L, 120L, "verified.exe", "--verified", @"C:\fixture\verified.exe", "1");
        Assert.True(sample.HasValue);
        Assert.Equal("verified", sample!.Value.Name);
        Assert.Equal("--verified", sample.Value.CommandLine);
        Assert.Equal(@"C:\fixture\verified.exe", sample.Value.Path);
        Assert.Equal(123L, sample.Value.StartTimeUtcTicks);
    }

    private sealed class ControlledInstance : IRestartProcess
    {
        public long StartTimeUtcTicks { get; set; }
        public bool HasExited { get; set; }
        public bool Killed { get; private set; }
        public bool Disposed { get; private set; }
        public void Kill() => Killed = true;
        public bool WaitForExit(int milliseconds) => HasExited = true;
        public void Dispose() => Disposed = true;
    }
}
