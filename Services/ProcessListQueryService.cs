using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Management;

namespace KillerShell.Services
{
    internal static class ProcessListQueryService
    {
        internal static Dictionary<int, (string Name, string CommandLine, string Path, string ParentPid, long? CreatedUtcTicks)> QueryProcesses()
        {
            var result = new Dictionary<int, (string, string, string, string, long?)>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessId, Name, ParentProcessId, CommandLine, ExecutablePath, CreationDate FROM Win32_Process");
                using var rows = searcher.Get();
                foreach (ManagementObject row in rows.Cast<ManagementObject>())
                {
                    using (row)
                    {
                        int pid = Convert.ToInt32(row["ProcessId"]);
                        string commandLine = row["CommandLine"] as string ?? string.Empty;
                        string path = row["ExecutablePath"] as string ?? string.Empty;
                        string parentPid = row["ParentProcessId"] is { } parent
                            ? Convert.ToInt32(parent).ToString(CultureInfo.InvariantCulture)
                            : "-";
                        long? created = null;
                        try
                        {
                            if (row["CreationDate"] is string creationDate)
                                created = ManagementDateTimeConverter.ToDateTime(creationDate).ToUniversalTime().Ticks;
                        }
                        catch (ArgumentException) { }
                        result[pid] = (row["Name"] as string ?? string.Empty, commandLine, path, parentPid, created);
                    }
                }
            }
            catch
            {
                // WMI may be unavailable or restricted. The caller displays empty details.
            }

            return result;
        }

        internal static string QueryOwner(int pid, long expectedCreatedUtcTicks)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT Handle, CreationDate FROM Win32_Process WHERE ProcessId = {pid}");
                using var rows = searcher.Get();
                foreach (ManagementObject row in rows.Cast<ManagementObject>())
                {
                    using (row)
                    {
                        bool firstRead = true;
                        return ReadOwnerForIdentity(expectedCreatedUtcTicks, () =>
                            {
                                if (!firstRead) row.Get();
                                firstRead = false;
                                return row["CreationDate"] is string created
                                    ? ManagementDateTimeConverter.ToDateTime(created).ToUniversalTime().Ticks : (long?)null;
                            }, () =>
                            {
                                var arguments = new object[2];
                                uint result = (uint)row.InvokeMethod("GetOwner", arguments);
                                if (result != 0 || arguments[0] is not string user || string.IsNullOrEmpty(user)) return "-";
                                string domain = arguments[1] as string ?? string.Empty;
                                return domain.Length > 0 ? domain + "\\" + user : user;
                            });
                    }
                }
            }
            catch
            {
                // Protected processes and restricted WMI access have no visible owner.
            }

            return "-";
        }

        internal static string ReadOwnerForIdentity(long expectedCreatedUtcTicks,
            Func<long?> readCreatedUtcTicks, Func<string> readOwner)
        {
            if (!ProcessListLogic.MatchesCreationTime(expectedCreatedUtcTicks, readCreatedUtcTicks())) return "-";
            string owner = readOwner();
            return ProcessListLogic.MatchesCreationTime(expectedCreatedUtcTicks, readCreatedUtcTicks()) ? owner : "-";
        }

        internal static Dictionary<string, (string StartMode, string Path, string LogOnAs, string Description)> QueryServices()
        {
            var result = new Dictionary<string, (string, string, string, string)>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, StartMode, PathName, StartName, Description FROM Win32_Service");
                using var rows = searcher.Get();
                foreach (ManagementObject row in rows.Cast<ManagementObject>())
                {
                    using (row)
                    {
                        string name = row["Name"] as string ?? string.Empty;
                        if (name.Length == 0) continue;
                        result[name] = (
                            row["StartMode"] as string ?? string.Empty,
                            row["PathName"] as string ?? string.Empty,
                            row["StartName"] as string ?? string.Empty,
                            row["Description"] as string ?? string.Empty);
                    }
                }
            }
            catch
            {
                // ServiceController data remains usable when WMI details are unavailable.
            }

            return result;
        }
    }
}
