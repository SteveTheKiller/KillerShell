using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    [CollectionDefinition("Process restart", DisableParallelization = true)]
    public sealed class ProcessRestartCollection { }

    [Collection("Process restart")]
    public sealed class ProcessRestartTests
    {
        [Theory]
        [InlineData(1223)]
        [InlineData(5)]
        [InlineData(2)]
        public async Task RefusedElevationKeepsTheOriginalRunning(int errorCode)
        {
            using var original = new ControlledProcess();
            original.Release.Set();
            await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => ProcessRestart.RestartAsync(42,
                new ProcessStartInfo("fixture") { UseShellExecute = true, Verb = "runas" },
                open: _ => original, start: _ => throw new System.ComponentModel.Win32Exception(errorCode),
                expectedStartTimeUtcTicks: 123));

            Assert.False(original.Killed, "Refusing elevation must not terminate the original process.");
            Assert.False(original.HasExited);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task DeclinedDestructiveConfirmationDoesNotRequestARestart(bool elevated)
        {
            using var original = new ControlledProcess();
            original.Release.Set();
            bool launched = false;
            bool restarted = await ProcessRestart.RestartAsync(42,
                new ProcessStartInfo("fixture") { UseShellExecute = true, Verb = elevated ? "runas" : "" },
                open: _ => original, start: _ => launched = true, expectedStartTimeUtcTicks: 123,
                confirm: () => false);

            Assert.False(restarted);
            Assert.False(original.Killed);
            Assert.False(launched);
        }

        [Fact]
        public async Task ReplacementWaitsForConfirmedExit()
        {
            using var original = new ControlledProcess();
            bool launched = false;
            var work = ProcessRestart.RestartAsync(42, new ProcessStartInfo("fixture"), 10000,
                _ => original, _ => { Assert.True(original.HasExited); launched = true; }, expectedStartTimeUtcTicks: 123);
            try
            {
                Assert.Same(original.Waiting.Task,
                    await Task.WhenAny(original.Waiting.Task, work, Task.Delay(10000)));
                Assert.False(launched);
                Assert.False(work.IsCompleted);
            }
            finally { original.Release.Set(); }
            await work;
            Assert.True(launched);
        }

        [Fact]
        public async Task TimeoutDoesNotLaunchReplacement()
        {
            using var original = new ControlledProcess();
            bool launched = false;
            await Assert.ThrowsAsync<TimeoutException>(() => ProcessRestart.RestartAsync(42,
                new ProcessStartInfo("fixture"), 1, _ => original, _ => launched = true, expectedStartTimeUtcTicks: 123));
            Assert.False(launched);
        }

        [Fact]
        public async Task TerminationRefusalDoesNotLaunchReplacement()
        {
            using var original = new ControlledProcess { RefuseKill = true };
            bool launched = false;
            await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => ProcessRestart.RestartAsync(42,
                new ProcessStartInfo("fixture"), 1, _ => original, _ => launched = true, expectedStartTimeUtcTicks: 123));
            Assert.False(launched);
        }

        [Fact]
        public async Task MissingOriginalCannotLaunchReplacement()
        {
            bool launched = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => ProcessRestart.RestartAsync(
                int.MaxValue, new ProcessStartInfo("fixture"), open: _ => null,
                start: _ => launched = true, expectedStartTimeUtcTicks: 123));
            Assert.False(launched);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task NativeRestartReleasesSingletonBeforeReplacementStarts(bool approvedHelper)
        {
            string mutex = "Local\\KillerShell-restart-" + Guid.NewGuid().ToString("N");
            string script = "$m = New-Object Threading.Mutex($false, '" + mutex + "'); " +
                "if (!$m.WaitOne(0)) { exit 9 }; [Console]::WriteLine('ready'); [Console]::Out.Flush(); Start-Sleep -Seconds 60";
            var info = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe"), "-NoProfile -NonInteractive -EncodedCommand " +
                Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var original = Process.Start(info)!;
            Process? replacement = null;
            try
            {
                var ready = original.StandardOutput.ReadLineAsync();
                Assert.Same(ready, await Task.WhenAny(ready, Task.Delay(10000)));
                Assert.Equal("ready", await ready);
                void Launch(ProcessStartInfo next)
                {
                    Assert.True(original.HasExited);
                    next.CreateNoWindow = true;
                    next.RedirectStandardOutput = true;
                    next.RedirectStandardError = true;
                    replacement = Process.Start(next);
                }
                if (approvedHelper)
                {
                    var broker = ProcessRestart.CreateBrokerStartInfo(original.Id, info, original.StartTime.ToUniversalTime().Ticks);
                    Assert.Equal(0, ProcessRestart.RunElevatedHelper(broker.Arguments.Split(' '), isElevated: true, start: Launch));
                }
                else
                    await ProcessRestart.RestartAsync(original.Id, info,
                        expectedStartTimeUtcTicks: original.StartTime.ToUniversalTime().Ticks, start: Launch);
                Assert.NotNull(replacement);
                ready = replacement!.StandardOutput.ReadLineAsync();
                Assert.Same(ready, await Task.WhenAny(ready, Task.Delay(10000)));
                Assert.Equal("ready", await ready);
            }
            finally
            {
                if (!original.HasExited) { original.Kill(); original.WaitForExit(5000); }
                if (replacement != null)
                {
                    if (!replacement.HasExited) { replacement.Kill(); replacement.WaitForExit(5000); }
                    replacement.Dispose();
                }
            }
        }

        private sealed class ControlledProcess : IRestartProcess
        {
            public long StartTimeUtcTicks => 123;
            public string ImagePath => @"C:\fixture\original.exe";
            internal readonly TaskCompletionSource<bool> Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly ManualResetEventSlim Release = new(false);
            public bool HasExited { get; private set; }
            internal bool Killed;
            internal bool RefuseKill;
            public void Kill() { if (RefuseKill) throw new System.ComponentModel.Win32Exception(5); Killed = true; }
            public bool WaitForExit(int milliseconds)
            {
                Waiting.TrySetResult(true);
                return HasExited = Release.Wait(milliseconds);
            }
            public void Dispose() { }
        }
    }
}
