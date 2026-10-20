using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace KillerShell.Services
{
    // A held process handle keeps termination and exit observation on the same instance.
    internal interface IRestartProcess : IDisposable
    {
        bool HasExited { get; }
        void Kill();
        bool WaitForExit(int milliseconds);
    }

    internal static class ProcessRestart
    {
        internal static Task RestartAsync(int pid, ProcessStartInfo replacement, int timeoutMilliseconds = 10000,
            Func<int, IRestartProcess?>? open = null, Action<ProcessStartInfo>? start = null)
        {
            return Task.Run(() =>
            {
                using var original = (open ?? Open)(pid);
                if (original != null && !original.HasExited)
                {
                    try { original.Kill(); }
                    catch (InvalidOperationException) when (original.HasExited) { }
                    if (!original.WaitForExit(timeoutMilliseconds) || !original.HasExited)
                        throw new TimeoutException();
                }
                (start ?? Start)(replacement);
            });
        }

        private static IRestartProcess? Open(int pid)
        {
            try { return new HeldProcess(Process.GetProcessById(pid)); }
            catch (ArgumentException) { return null; }
        }

        private static void Start(ProcessStartInfo info)
        {
            using var process = Process.Start(info);
            if (process == null) throw new InvalidOperationException("The replacement process could not start.");
        }

        private sealed class HeldProcess : IRestartProcess
        {
            private readonly Process _process;
            internal HeldProcess(Process process)
            {
                _process = process;
                try { _ = process.Handle; }
                catch { process.Dispose(); throw; }
            }
            public bool HasExited => _process.HasExited;
            public void Kill() => _process.Kill();
            public bool WaitForExit(int milliseconds) => _process.WaitForExit(milliseconds);
            public void Dispose() => _process.Dispose();
        }
    }
}
