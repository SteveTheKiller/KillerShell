using System;
using System.Diagnostics;
using System.CodeDom.Compiler;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KillerShell.Services;
using Microsoft.CSharp;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class ServiceFailureReportTests
{
    [Fact]
    public void NativeReportOpenStallCannotHoldServiceResultOrProcessExit()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShell-service-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Process? process = null;
        try
        {
            string report = Path.Combine(root, "service-actions.log");
            File.WriteAllText(report, "existing report");
            string assembly = typeof(ServiceFailureChild).Assembly.Location.Replace("\"", "\"\"");
            string product = typeof(ServiceControlService).Assembly.Location.Replace("\"", "\"\"");
            string child = Path.Combine(root, "StalledReportCaller.exe");
            using (var compiler = new CSharpCodeProvider())
            {
                var parameters = new CompilerParameters(new[] { "System.dll" }, child) { GenerateExecutable = true };
                string code = "using System; using System.Reflection; class Entry { static void Main(string[] args) { "
                    + "Assembly.LoadFrom(@\"" + product + "\"); var a = Assembly.LoadFrom(@\"" + assembly + "\"); "
                    + "a.GetType(\"KillerShell.Tests.Services.ServiceFailureChild\").GetMethod(\"RunStalled\").Invoke(null, new object[] { args[0] }); } }";
                Assert.False(compiler.CompileAssemblyFromSource(parameters, code).Errors.HasErrors);
            }
            using (var held = new NativeOplock(report))
            {
                process = Process.Start(new ProcessStartInfo { FileName = child, Arguments = "\"" + report + "\"", UseShellExecute = false, CreateNoWindow = true })!;
                Assert.True(held.BreakRequested.WaitOne(5000), "Report writer never reached a real native open.");
                Assert.True(process.WaitForExit(4500), "Stalled native report I/O held the service result and foreground process alive.");
                Assert.Equal(0, process.ExitCode);
                string result = File.ReadAllText(report + ".result");
                Assert.Contains("service unavailable", result);
                Assert.Contains("Timed out", result);
                Assert.StartsWith("False|False||", result);
            }
        }
        finally
        {
            if (process != null) { if (!process.WaitForExit(5000)) { process.Kill(); process.WaitForExit(); } process.Dispose(); }
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task LoggingFailureDoesNotReplaceTheServiceFailure()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShell-service-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string blockedParent = Path.Combine(root, "not-a-directory");
            File.WriteAllText(blockedParent, "preserve");
            var result = await ServiceControlService.ExecuteAsync("Fixture", () => throw new InvalidOperationException("service unavailable"),
                ServiceControlAction.Restart, CancellationToken.None, Path.Combine(blockedParent, "service-actions.log"));
            Assert.False(result.Succeeded);
            Assert.False(result.Canceled);
            Assert.Equal("service unavailable", result.Error);
            Assert.Empty(result.ReportPath);
            Assert.NotEmpty(result.ReportError);
            Assert.Equal("preserve", File.ReadAllText(blockedParent));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RepeatedFailuresStayBoundedAndKeepLatestSingleLineRecord()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShell-service-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string report = Path.Combine(root, "service-actions.log");
            string error = "broken\r\nforged\t" + new string('X', 12000);
            for (int i = 0; i < 30; i++)
            {
                var result = await ServiceControlService.ExecuteAsync("Fixture" + i, () => throw new InvalidOperationException(error),
                    ServiceControlAction.Restart, CancellationToken.None, report);
                Assert.Equal(report, result.ReportPath);
                Assert.Empty(result.ReportError);
                Assert.InRange(new FileInfo(report).Length, 1, 65536);
            }
            string text = File.ReadAllText(report);
            Assert.Contains("Fixture29", text);
            Assert.Contains(@"broken\r\nforged\t", text);
            Assert.DoesNotContain("\nforged", text);
            Assert.Contains("[truncated]", text);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RestartFailureSurvivesCallerProcessExitWithoutAwaitingResult()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShell-service-report-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string report = Path.Combine(root, "service-actions.log");
            string assembly = typeof(ServiceFailureChild).Assembly.Location.Replace("\"", "\"\"");
            string product = typeof(ServiceControlService).Assembly.Location.Replace("\"", "\"\"");
            string child = Path.Combine(root, "AbandonedCaller.exe");
            using (var compiler = new CSharpCodeProvider())
            {
                var parameters = new CompilerParameters(new[] { "System.dll" }, child) { GenerateExecutable = true };
                string code = "using System; using System.Reflection; class Entry { static void Main(string[] args) { "
                    + "Assembly.LoadFrom(@\"" + product + "\"); var a = Assembly.LoadFrom(@\"" + assembly + "\"); "
                    + "a.GetType(\"KillerShell.Tests.Services.ServiceFailureChild\").GetMethod(\"Run\").Invoke(null, new object[] { args[0] }); } }";
                var compiled = compiler.CompileAssemblyFromSource(parameters, code);
                Assert.False(compiled.Errors.HasErrors, string.Join(Environment.NewLine, compiled.Errors));
            }
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = child,
                Arguments = "\"" + report + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            Task<string> errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10000)) { process.Kill(); Assert.Fail("Abandoned caller process did not exit."); }
            Assert.True(process.ExitCode == 0, await errors);
            Assert.True(File.Exists(report), "Service failure vanished when the caller exited without awaiting its result.");
            string text = File.ReadAllText(report);
            Assert.Contains("FixtureRestart", text);
            Assert.Contains("Restart", text);
            Assert.Contains("start refused after caller exit", text);
            Assert.Matches(@"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}.*Z", text);
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class NativeOplock : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private readonly IntPtr _overlapped;
        internal EventWaitHandle BreakRequested { get; } = new(false, EventResetMode.ManualReset);
        internal NativeOplock(string path)
        {
            _handle = CreateFile(path, 0x80000000, 7, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (_handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            _overlapped = Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlappedData>());
            Marshal.StructureToPtr(new NativeOverlappedData { Event = BreakRequested.SafeWaitHandle.DangerousGetHandle() }, _overlapped, false);
            bool complete = DeviceIoControl(_handle, 0x00090000, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, _overlapped);
            if (complete || Marshal.GetLastWin32Error() != 997) throw new IOException("An exclusive NTFS oplock could not be established.");
        }
        public void Dispose()
        {
            _handle.Dispose();
            BreakRequested.WaitOne(2000);
            Marshal.FreeHGlobal(_overlapped);
            BreakRequested.Dispose();
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeOverlappedData { public IntPtr Internal, InternalHigh; public uint Offset, OffsetHigh; public IntPtr Event; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
}

public static class ServiceFailureChild
{
    public static void RunStalled(string report)
    {
        var result = ServiceControlService.ExecuteAsync("Fixture", () => throw new InvalidOperationException("service unavailable"),
            ServiceControlAction.Restart, CancellationToken.None, report).GetAwaiter().GetResult();
        File.WriteAllText(report + ".result", result.Succeeded + "|" + result.Canceled + "|" + result.ReportPath + "|" + result.ReportError + "|" + result.Error);
    }

    public static void Run(string report)
    {
        var service = new AbandonedService();
        var canceled = new CancellationTokenSource();
        _ = ServiceControlService.ExecuteAsync("FixtureRestart", () => service, ServiceControlAction.Restart, canceled.Token, report);
        if (!service.StopAccepted.Wait(5000)) throw new Exception("Fixture stop was never accepted.");
        canceled.Cancel();
        // No dispatcher, completion handler, or task wait remains in this caller.
    }

    private sealed class AbandonedService : IServiceHandle
    {
        public ManualResetEventSlim StopAccepted { get; } = new();
        public ServiceControllerStatus Status { get; private set; } = ServiceControllerStatus.Running;
        public void Refresh() { }
        public void Stop() { Status = ServiceControllerStatus.StopPending; StopAccepted.Set(); }
        public void Start() => throw new InvalidOperationException("start refused after caller exit");
        public void WaitForStatus(ServiceControllerStatus desired, TimeSpan timeout)
        {
            Thread.Sleep(300);
            Status = desired;
        }
        public void Dispose() { }
    }
}
