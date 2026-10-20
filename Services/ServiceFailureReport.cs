using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KillerShell.Services
{
    internal static class ServiceFailureReport
    {
        private const int MaximumBytes = 64 * 1024;

        internal static void TryWrite(string serviceName, ServiceControlAction action, string error,
            string? pathOverride, out string savedPath, out string reportError)
        {
            savedPath = string.Empty;
            reportError = string.Empty;
            try
            {
                var complete = new TaskCompletionSource<ReportResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                var writer = new Thread(() => complete.SetResult(Write(serviceName, action, error, pathOverride)))
                {
                    IsBackground = true,
                    Name = "KillerShell service failure report",
                };
                writer.Start();
                if (!complete.Task.Wait(TimeSpan.FromSeconds(2)))
                {
                    reportError = "Timed out writing the service failure report. The background write may finish later.";
                    return;
                }
                ReportResult result = complete.Task.GetAwaiter().GetResult();
                savedPath = result.Path;
                reportError = result.Error;
            }
            catch (Exception ex) { reportError = ex.Message; }
        }

        private readonly struct ReportResult(string path, string error)
        {
            internal string Path { get; } = path;
            internal string Error { get; } = error;
        }

        private static ReportResult Write(string serviceName, ServiceControlAction action, string error, string? pathOverride)
        {
            try
            {
                string path = Path.GetFullPath(pathOverride ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "KillerShell", "service-actions.log"));
                string record = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                    + "\tservice=" + OneLine(serviceName, 256) + "\taction=" + action
                    + "\terror=" + OneLine(error, 4096) + Environment.NewLine;
                byte[] bytes = new UTF8Encoding(false).GetBytes(record);
                string mutexName;
                using (var hash = SHA256.Create())
                    mutexName = "Local\\KillerShell-service-report-"
                        + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))).Replace("-", "");
                using var mutex = new Mutex(false, mutexName);
                bool owned;
                try { owned = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned) throw new IOException("Timed out waiting to write the service failure report.");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write,
                        FileShare.Read, 4096, FileOptions.WriteThrough);
                    // Retain recent failures without allowing this diagnostic file to grow forever.
                    if (stream.Length + bytes.Length > MaximumBytes) stream.SetLength(0);
                    stream.Position = stream.Length;
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                finally { mutex.ReleaseMutex(); }
                return new ReportResult(path, string.Empty);
            }
            catch (Exception ex) { return new ReportResult(string.Empty, ex.Message); }
        }

        private static string OneLine(string text, int maximum)
        {
            string value = (text ?? string.Empty).Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            return value.Length <= maximum ? value : value.Substring(0, maximum) + " [truncated]";
        }
    }
}
