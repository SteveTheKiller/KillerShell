using System;
using System.Collections.Generic;
using System.Linq;
using KillerShell.Models;

namespace KillerShell.Services
{
    internal static class ProcessListLogic
    {
        internal sealed class ProcessCache
        {
            private readonly object _gate = new();
            private readonly Dictionary<int, CachedProcess> _entries = new();

            internal CachedProcess Observe(int pid, long? createdUtcTicks)
            {
                lock (_gate)
                {
                    if (_entries.TryGetValue(pid, out var entry) &&
                        MatchesCreationTime(entry.CreatedUtcTicks, createdUtcTicks)) return entry;
                    return _entries[pid] = new CachedProcess(pid, createdUtcTicks);
                }
            }

            internal string Owner(CachedProcess entry)
            {
                lock (_gate) return entry.Owner ?? "-";
            }

            internal bool BeginOwnerLookup(CachedProcess entry)
            {
                lock (_gate)
                {
                    if (!entry.CreatedUtcTicks.HasValue || !IsCurrent(entry) ||
                        entry.Owner != null || entry.OwnerPending) return false;
                    entry.OwnerPending = true;
                    return true;
                }
            }

            internal bool CompleteOwnerLookup(CachedProcess request, string owner)
            {
                lock (_gate)
                {
                    if (!_entries.TryGetValue(request.Pid, out var current) ||
                        !ReferenceEquals(current, request)) return false;
                    current.Owner = owner;
                    current.OwnerPending = false;
                    return true;
                }
            }

            internal bool IsCurrent(CachedProcess entry)
            {
                lock (_gate) return _entries.TryGetValue(entry.Pid, out var current) && ReferenceEquals(current, entry);
            }

            internal void ForgetExcept(ISet<int> livePids)
            {
                lock (_gate)
                    foreach (int pid in _entries.Keys.Where(pid => !livePids.Contains(pid)).ToList())
                        _entries.Remove(pid);
            }
        }

        internal sealed class CachedProcess
        {
            internal int Pid { get; }
            internal long? CreatedUtcTicks { get; }
            internal bool Unreadable { get; set; }
            internal string? Owner;
            internal bool OwnerPending;
            private TimeSpan? _lastCpu;
            private DateTime _lastSampleAt;

            internal CachedProcess(int pid, long? createdUtcTicks)
            {
                Pid = pid;
                CreatedUtcTicks = createdUtcTicks;
            }

            internal double SampleCpu(TimeSpan cpu, DateTime now, int processorCount)
            {
                double result = 0;
                double elapsed = (now - _lastSampleAt).TotalMilliseconds;
                if (_lastCpu.HasValue && elapsed > 0)
                    result = Math.Max(0, Math.Round((cpu - _lastCpu.Value).TotalMilliseconds /
                        elapsed / processorCount * 100.0, 1));
                _lastCpu = cpu;
                _lastSampleAt = now;
                return result;
            }
        }

        internal static bool Matches(ProcessInfo process, string query)
            => query.Length == 0
               || Contains(process.Name, query)
               || Contains(process.Path, query)
               || Contains(process.User, query);

        internal static bool Matches(ServiceInfo service, string query)
            => query.Length == 0
               || Contains(service.Name, query)
               || Contains(service.DisplayName, query)
               || Contains(service.Path, query)
               || Contains(service.LogOnAs, query);

        internal static string FriendlyStartMode(string raw) => raw switch
        {
            "Auto" => "Automatic",
            "Manual" => "Manual",
            "Disabled" => "Disabled",
            "Boot" => "Boot",
            "System" => "System",
            _ => raw,
        };

        internal static string ExtractExePath(string pathName)
        {
            if (string.IsNullOrEmpty(pathName)) return string.Empty;
            string value = pathName.Trim();
            if (value.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = value.IndexOf('"', 1);
                return end > 0 ? value[1..end] : value.Trim('"');
            }

            int space = value.IndexOf(' ');
            return space > 0 ? value[..space] : value;
        }

        internal static string ExtractArguments(string commandLine)
        {
            if (string.IsNullOrEmpty(commandLine)) return string.Empty;

            string rest = commandLine;
            if (rest.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = rest.IndexOf('"', 1);
                rest = end > 0 ? rest[(end + 1)..] : string.Empty;
            }
            else
            {
                int space = rest.IndexOf(' ');
                rest = space > 0 ? rest[(space + 1)..] : string.Empty;
            }

            return rest.Trim();
        }

        /// <summary>
        /// Opens the process and holds its handle, so the id cannot be handed to a new process
        /// while a confirm dialog is up. Windows only reuses an id once every handle to the old
        /// process is closed. Null when it is gone or cannot be opened (a protected process).
        /// </summary>
        internal static System.Diagnostics.Process? PinProcess(int pid, long? expectedStartTimeUtcTicks)
        {
            if (!expectedStartTimeUtcTicks.HasValue) return null;
            System.Diagnostics.Process? proc = null;
            try
            {
                proc = System.Diagnostics.Process.GetProcessById(pid);
                _ = proc.Handle;   // opened now and cached by the Process object for Kill
                if (proc.HasExited || proc.StartTime.ToUniversalTime().Ticks != expectedStartTimeUtcTicks.Value)
                {
                    proc.Dispose();
                    return null;
                }
                return proc;
            }
            catch
            {
                proc?.Dispose();
                return null;
            }
        }

        /// <summary>
        /// Whether a refreshed sample may keep the row already on screen. A known identity must
        /// be the same cache entry; otherwise the PID was reused and the row is rebuilt. When
        /// neither sample has a creation time (the Idle process, or one that cannot be opened
        /// and WMI has not reported yet), each refresh gets a fresh cache entry by design, so
        /// cached CPU and owner never cross a possible reuse - but rebuilding the row every
        /// tick made it flicker, drop its selection and lose its place. Its fields are all
        /// rewritten from the new sample anyway, so the row itself can stay.
        /// </summary>
        internal static bool KeepsRow(CachedProcess displayed, CachedProcess sample)
            => ReferenceEquals(displayed, sample)
               || (displayed.Pid == sample.Pid && displayed.CreatedUtcTicks == null && sample.CreatedUtcTicks == null);

        // WMI's datetime format records microseconds; the held process time has 100 ns ticks.
        internal static bool MatchesCreationTime(long? sampledUtcTicks, long? metadataUtcTicks)
            => sampledUtcTicks.HasValue && metadataUtcTicks.HasValue &&
               sampledUtcTicks.Value / 10 == metadataUtcTicks.Value / 10;

        internal static (string Name, string CommandLine, string Path, string ParentPid, long StartTimeUtcTicks)?
            VerifySampleMetadata(long? heldUtcTicks, long? metadataUtcTicks, string name,
                                 string commandLine, string path, string parentPid)
        {
            if (name.Length == 0 || !MatchesCreationTime(heldUtcTicks, metadataUtcTicks)) return null;
            return (System.IO.Path.GetFileNameWithoutExtension(name), commandLine, path, parentPid, heldUtcTicks!.Value);
        }

        private static bool Contains(string value, string query)
            => value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
