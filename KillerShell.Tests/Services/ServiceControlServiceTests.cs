using System;
using System.Collections.Generic;
using System.IO;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    /// <summary>
    /// Drives service actions through a stand-in for the Windows service manager, the one
    /// boundary these tests replace. Closing the tab that asked for a restart cancels its token;
    /// once Stop has been accepted, the restart must still finish with Start.
    /// </summary>
    public sealed class ServiceControlServiceTests : IDisposable
    {
        private readonly string _reportRoot = Path.Combine(Path.GetTempPath(), "KillerShell-service-tests-" + Guid.NewGuid().ToString("N"));
        private string ReportPath => Path.Combine(_reportRoot, "service-actions.log");
        public void Dispose() { if (Directory.Exists(_reportRoot)) Directory.Delete(_reportRoot, true); }
        [Fact]
        public async Task Restart_StartsTheServiceEvenWhenCanceledAfterStop()
        {
            var service = new FakeService(ServiceControllerStatus.Running) { HoldStop = true };
            using var cts = new CancellationTokenSource();

            Task<ServiceControlResult> run = ServiceControlService.ExecuteAsync(() => service, ServiceControlAction.Restart, cts.Token);
            Assert.True(service.StopIssued.Wait(5000), "Stop was never issued.");
            cts.Cancel();                 // the owning tab closes while the service is stopping
            service.ReleaseStop.Set();

            ServiceControlResult result = await WithTimeout(run);
            Assert.True(result.Succeeded, "Restart did not complete: " + result.Error);
            Assert.False(result.Canceled);
            Assert.Equal(new[] { "Stop", "Start" }, service.Calls);
            Assert.Equal(ServiceControllerStatus.Running, service.Status);
        }

        [Fact]
        public async Task CancelBeforeAnythingHappens_ChangesNothing()
        {
            var service = new FakeService(ServiceControllerStatus.Running);
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            ServiceControlResult result = await WithTimeout(ServiceControlService.ExecuteAsync("Fixture", () => service, ServiceControlAction.Restart, cts.Token, ReportPath));
            Assert.True(result.Canceled);
            Assert.Empty(service.Calls);
            Assert.Equal(ServiceControllerStatus.Running, service.Status);
            Assert.False(File.Exists(ReportPath));
            Assert.Empty(result.ReportPath);
            Assert.Empty(result.ReportError);
        }

        [Fact]
        public async Task StartFailureAfterCanceledStop_IsReportedNotSwallowed()
        {
            var service = new FakeService(ServiceControllerStatus.Running) { HoldStop = true, FailStart = true };
            using var cts = new CancellationTokenSource();

            Task<ServiceControlResult> run = ServiceControlService.ExecuteAsync("Fixture", () => service, ServiceControlAction.Restart, cts.Token, ReportPath);
            Assert.True(service.StopIssued.Wait(5000));
            cts.Cancel();
            service.ReleaseStop.Set();

            ServiceControlResult result = await WithTimeout(run);
            Assert.False(result.Succeeded);
            Assert.False(result.Canceled, "A failed start after an accepted stop was reported as a cancellation.");
            Assert.Contains("start refused", result.Error);
            Assert.Equal(ReportPath, result.ReportPath);
            Assert.Empty(result.ReportError);
            Assert.Contains("start refused", File.ReadAllText(ReportPath));
        }

        [Fact]
        public async Task StopTimeout_IsReportedAndStartIsNotAttempted()
        {
            var service = new FakeService(ServiceControllerStatus.Running) { StopNeverFinishes = true };
            ServiceControlResult result = await WithTimeout(ServiceControlService.ExecuteAsync("Fixture", () => service, ServiceControlAction.Restart, CancellationToken.None, ReportPath));
            Assert.False(result.Succeeded);
            Assert.False(result.Canceled);
            Assert.Equal(new[] { "Stop" }, service.Calls);
        }

        [Fact]
        public async Task RestartOfAStoppedService_JustStarts()
        {
            var service = new FakeService(ServiceControllerStatus.Stopped);
            ServiceControlResult result = await WithTimeout(ServiceControlService.ExecuteAsync(() => service, ServiceControlAction.Restart, CancellationToken.None));
            Assert.True(result.Succeeded);
            Assert.Equal(new[] { "Start" }, service.Calls);
        }

        [Fact]
        public async Task ServiceWork_RunsOnAThreadThatKeepsTheProcessAlive()
        {
            // A background thread dies with the app, which could leave a service stopped halfway
            // through a restart when the window closes.
            var service = new FakeService(ServiceControllerStatus.Running);
            ServiceControlResult result = await WithTimeout(ServiceControlService.ExecuteAsync(() => service, ServiceControlAction.Restart, CancellationToken.None));
            Assert.True(result.Succeeded);
            Assert.False(service.StopRanOnBackgroundThread, "Service work ran on a background thread.");
        }

        private static async Task<ServiceControlResult> WithTimeout(Task<ServiceControlResult> task)
        {
            Task finished = await Task.WhenAny(task, Task.Delay(10000));
            Assert.True(finished == task, "The service action did not finish.");
            return await task;
        }

        private sealed class FakeService : IServiceHandle
        {
            public FakeService(ServiceControllerStatus status) => Status = status;

            public ServiceControllerStatus Status { get; private set; }
            public List<string> Calls { get; } = new();
            public bool HoldStop { get; set; }
            public bool FailStart { get; set; }
            public bool StopNeverFinishes { get; set; }
            public bool StopRanOnBackgroundThread { get; private set; }
            public ManualResetEventSlim StopIssued { get; } = new();
            public ManualResetEventSlim ReleaseStop { get; } = new();

            public void Refresh() { }

            public void Stop()
            {
                Calls.Add("Stop");
                StopRanOnBackgroundThread = Thread.CurrentThread.IsBackground;
                Status = ServiceControllerStatus.StopPending;
                StopIssued.Set();
            }

            public void Start()
            {
                Calls.Add("Start");
                if (FailStart) throw new InvalidOperationException("start refused");
                Status = ServiceControllerStatus.Running;
            }

            public void WaitForStatus(ServiceControllerStatus desired, TimeSpan timeout)
            {
                if (desired == ServiceControllerStatus.Stopped)
                {
                    if (StopNeverFinishes) throw new System.ServiceProcess.TimeoutException("stop timed out");
                    if (HoldStop && !ReleaseStop.Wait(5000)) throw new System.ServiceProcess.TimeoutException("test never released stop");
                    Status = ServiceControllerStatus.Stopped;
                }
                if (Status != desired) throw new System.ServiceProcess.TimeoutException("status is " + Status);
            }

            public void Dispose() { }
        }
    }
}
