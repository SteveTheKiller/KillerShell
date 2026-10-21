using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace KillerShell.Services
{
    // A held process handle keeps termination and exit observation on the same instance.
    internal interface IRestartProcess : IDisposable
    {
        long StartTimeUtcTicks { get; }
        string ImagePath { get; }
        bool HasExited { get; }
        void Kill();
        bool WaitForExit(int milliseconds);
    }

    internal static class ProcessRestart
    {
        internal const string ElevatedArgument = "--restart-process-elevated";

        internal static Task<bool> RestartAsync(int pid, ProcessStartInfo replacement, int timeoutMilliseconds = 10000,
            Func<int, IRestartProcess?>? open = null, Action<ProcessStartInfo>? start = null,
            long? expectedStartTimeUtcTicks = null, string? expectedImagePath = null,
            Func<string, IDisposable>? holdImage = null, Func<bool>? confirm = null)
        {
            if (confirm != null && !confirm()) return Task.FromResult(false);
            return Task.Run(() =>
            {
                if (string.Equals(replacement.Verb, "runas", StringComparison.OrdinalIgnoreCase))
                {
                    if (!expectedStartTimeUtcTicks.HasValue) throw new InvalidOperationException();
                    (start ?? LaunchBroker)(CreateBrokerStartInfo(pid, replacement, expectedStartTimeUtcTicks.Value,
                        timeoutMilliseconds));
                    return true;
                }
                using var original = (open ?? Open)(pid);
                if (original == null || original.HasExited || !expectedStartTimeUtcTicks.HasValue ||
                    original.StartTimeUtcTicks != expectedStartTimeUtcTicks.Value)
                    throw new InvalidOperationException();
                if (expectedImagePath != null && !string.Equals(Path.GetFullPath(original.ImagePath),
                    Path.GetFullPath(expectedImagePath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The process executable changed before restart.");
                using var image = expectedImagePath == null ? null :
                    (holdImage ?? (path => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)))(expectedImagePath);
                if (!original.HasExited)
                {
                    try { original.Kill(); }
                    catch (InvalidOperationException) when (original.HasExited) { }
                    if (!original.WaitForExit(timeoutMilliseconds) || !original.HasExited)
                        throw new TimeoutException();
                }
                (start ?? Start)(replacement);
                return true;
            });
        }

        internal static ProcessStartInfo CreateBrokerStartInfo(int pid, ProcessStartInfo replacement,
            long expectedStartTimeUtcTicks, int timeoutMilliseconds = 10000)
        {
            string Encode(string value) => "b" + Convert.ToBase64String(Encoding.Unicode.GetBytes(value));
            using var current = Process.GetCurrentProcess();
            return new ProcessStartInfo(current.MainModule!.FileName,
                string.Join(" ", ElevatedArgument, pid.ToString(CultureInfo.InvariantCulture),
                    expectedStartTimeUtcTicks.ToString(CultureInfo.InvariantCulture),
                    timeoutMilliseconds.ToString(CultureInfo.InvariantCulture), Encode(replacement.FileName),
                    Encode(replacement.Arguments), Encode(replacement.WorkingDirectory)))
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        }

        internal static int RunElevatedHelper(string[] arguments, bool? isElevated = null,
            Func<int, IRestartProcess?>? open = null, Action<ProcessStartInfo>? start = null,
            Func<string, IDisposable>? holdImage = null)
        {
            try
            {
                if (!(isElevated ?? KillerFamily.Deployment.InstallerExitCodes.IsElevated()))
                    return KillerFamily.Deployment.InstallerExitCodes.AccessDenied;
                if (arguments.Length != 7 || arguments[0] != ElevatedArgument
                    || !int.TryParse(arguments[1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0
                    || !long.TryParse(arguments[2], NumberStyles.None, CultureInfo.InvariantCulture, out long created) || created <= 0
                    || !int.TryParse(arguments[3], NumberStyles.None, CultureInfo.InvariantCulture, out int timeout)
                    || timeout <= 0 || timeout > 60000)
                    return KillerFamily.Deployment.InstallerExitCodes.SecurityRejected;
                string Decode(string value)
                {
                    if (!value.StartsWith("b", StringComparison.Ordinal)) throw new FormatException();
                    string text = new UnicodeEncoding(false, false, true).GetString(Convert.FromBase64String(value.Substring(1)));
                    if (text.IndexOf('\0') >= 0) throw new FormatException();
                    return text;
                }
                string path = Decode(arguments[4]);
                string args = Decode(arguments[5]);
                string directory = Decode(arguments[6]);
                bool Absolute(string value) => Path.IsPathRooted(value)
                    && string.Equals(Path.GetFullPath(value), value, StringComparison.OrdinalIgnoreCase);
                if (!Absolute(path) || (directory.Length > 0 && !Absolute(directory)))
                    return KillerFamily.Deployment.InstallerExitCodes.SecurityRejected;
                var replacement = new ProcessStartInfo(path, args) { UseShellExecute = false, WorkingDirectory = directory };
                RestartAsync(pid, replacement, timeout, open, start, created, path, holdImage).GetAwaiter().GetResult();
                return KillerFamily.Deployment.InstallerExitCodes.Success;
            }
            catch (FormatException) { return KillerFamily.Deployment.InstallerExitCodes.SecurityRejected; }
            catch (DecoderFallbackException) { return KillerFamily.Deployment.InstallerExitCodes.SecurityRejected; }
            catch (Exception error) { return KillerFamily.Deployment.InstallerExitCodes.FromException(error); }
        }

        internal static void LaunchBroker(ProcessStartInfo info)
        {
            using var child = Process.Start(info);
            if (child == null) throw new InvalidOperationException("The elevated restart helper could not start.");
            if (!child.WaitForExit(120000))
            {
                // Only this held helper is stopped, so it cannot terminate the original later.
                child.Kill();
                child.WaitForExit(5000);
                throw new TimeoutException("The elevated restart helper did not finish.");
            }
            if (child.ExitCode != 0) throw new System.ComponentModel.Win32Exception(child.ExitCode);
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
            public long StartTimeUtcTicks => _process.StartTime.ToUniversalTime().Ticks;
            public string ImagePath => _process.MainModule?.FileName ?? string.Empty;
            public void Kill() => _process.Kill();
            public bool WaitForExit(int milliseconds) => _process.WaitForExit(milliseconds);
            public void Dispose() => _process.Dispose();
        }
    }
}
