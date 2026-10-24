using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
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
                    using var stream = OpenWithoutLinks(path);
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

        /// <summary>
        /// Opens the log for appending without letting a link redirect it. Service actions
        /// usually run in the elevated window, and the log lives under %LOCALAPPDATA%, which
        /// any process of the same user can change at medium integrity. Swapping the folder for
        /// a junction (to \RPC Control plus an object-manager symlink, the classic trick) would
        /// otherwise turn this elevated open, truncate and append into a write to any file on
        /// the machine. So: no folder on the way may be a reparse point, the file itself is
        /// opened without following one, and the handle's final path must be the path asked
        /// for before a single byte is truncated or written.
        /// </summary>
        private static FileStream OpenWithoutLinks(string path)
        {
            for (string? folder = Path.GetDirectoryName(path); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
            {
                if (Path.GetPathRoot(folder) == folder) break;
                if ((File.GetAttributes(folder!) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The service failure report folder is a link and was not written: " + folder);
            }

            var handle = CreateFileW(path, GenericWrite, FileShareRead, IntPtr.Zero, OpenAlways,
                FileFlagOpenReparsePoint | FileFlagWriteThrough, IntPtr.Zero);
            if (handle.IsInvalid) throw new IOException(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message);
            try
            {
                var buffer = new StringBuilder(1024);
                uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity)
                    throw new IOException("The service failure report's location could not be verified.");
                string final = buffer.ToString();
                if (final.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) final = @"\\" + final.Substring(8);
                else if (final.StartsWith(@"\\?\", StringComparison.Ordinal)) final = final.Substring(4);
                if (!string.Equals(final, LongPath(path), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The service failure report resolved somewhere else and was not written: " + final);
                return new FileStream(handle, FileAccess.Write, 4096);
            }
            catch { handle.Dispose(); throw; }
        }

        // The final path is always spelled out in full, so an 8.3 short name in the path asked
        // for (GetTempPath can return one) is expanded before the two are compared.
        private static string LongPath(string path)
        {
            var buffer = new StringBuilder(1024);
            uint length = GetLongPathNameW(path, buffer, (uint)buffer.Capacity);
            return length == 0 || length >= buffer.Capacity ? path : buffer.ToString();
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint length);

        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x1;
        private const uint OpenAlways = 4;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileFlagWriteThrough = 0x80000000;

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share,
            IntPtr security, uint disposition, uint flags, IntPtr template);

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle file,
            StringBuilder path, uint length, uint flags);

        private static string OneLine(string text, int maximum)
        {
            string value = (text ?? string.Empty).Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
            return value.Length <= maximum ? value : value.Substring(0, maximum) + " [truncated]";
        }
    }
}
