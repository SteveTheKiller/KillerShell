using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class ProcessRestartTests
    {
        [Fact]
        public async Task ReplacementWaitsForConfirmedExit()
        {
            using var original = new ControlledProcess();
            bool launched = false;
            var work = ProcessRestart.RestartAsync(42, new ProcessStartInfo("fixture"), 1000,
                _ => original, _ => { Assert.True(original.HasExited); launched = true; });
            try
            {
                Assert.True(original.Waiting.Wait(2000), "Restart never waited for the original process.");
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
                new ProcessStartInfo("fixture"), 1, _ => original, _ => launched = true));
            Assert.False(launched);
        }

        [Fact]
        public async Task TerminationRefusalDoesNotLaunchReplacement()
        {
            using var original = new ControlledProcess { RefuseKill = true };
            bool launched = false;
            await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => ProcessRestart.RestartAsync(42,
                new ProcessStartInfo("fixture"), 1, _ => original, _ => launched = true));
            Assert.False(launched);
        }

        [Fact]
        public async Task AlreadyExitedCanRelaunchAndLaunchFailurePropagates()
        {
            bool launched = false;
            await ProcessRestart.RestartAsync(int.MaxValue, new ProcessStartInfo("fixture"),
                open: _ => null, start: _ => launched = true);
            Assert.True(launched);
            await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => ProcessRestart.RestartAsync(
                int.MaxValue, new ProcessStartInfo("ks-missing-" + Guid.NewGuid().ToString("N")) { UseShellExecute = false }));
        }

        [Fact]
        public async Task NativeRestartReleasesSingletonBeforeReplacementStarts()
        {
            string mutex = "Local\\KillerShell-restart-" + Guid.NewGuid().ToString("N");
            string script = "$m = New-Object Threading.Mutex($false, '" + mutex + "'); " +
                "if (!$m.WaitOne(0)) { exit 9 }; [Console]::WriteLine('ready'); [Console]::Out.Flush(); Start-Sleep -Seconds 60";
            var info = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " +
                Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script)))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var original = Process.Start(info)!;
            Process? replacement = null;
            try
            {
                var ready = original.StandardOutput.ReadLineAsync();
                Assert.Same(ready, await Task.WhenAny(ready, Task.Delay(10000)));
                Assert.Equal("ready", await ready);
                await ProcessRestart.RestartAsync(original.Id, info, start: next =>
                {
                    Assert.True(original.HasExited);
                    replacement = Process.Start(next);
                });
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
            internal readonly ManualResetEventSlim Waiting = new(false);
            internal readonly ManualResetEventSlim Release = new(false);
            public bool HasExited { get; private set; }
            internal bool RefuseKill;
            public void Kill() { if (RefuseKill) throw new System.ComponentModel.Win32Exception(5); }
            public bool WaitForExit(int milliseconds)
            {
                Waiting.Set();
                return HasExited = Release.Wait(milliseconds);
            }
            public void Dispose() { }
        }
    }
}
