using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Management;
using System.Threading;

namespace KillerShell.Services
{
    internal readonly struct PerformanceDiskInfo
    {
        internal string InstanceName { get; }
        internal string Model { get; }
        internal List<string> DriveLetters { get; }

        internal PerformanceDiskInfo(string instanceName, string model, List<string> driveLetters)
        {
            InstanceName = instanceName;
            Model = model;
            DriveLetters = driveLetters;
        }
    }

    internal readonly struct PerformanceHardwareInfo
    {
        internal string Cpu { get; }
        internal string Ram { get; }
        internal string Gpu { get; }
        internal string Network { get; }
        internal double TotalRamGb { get; }
        internal int CpuCores { get; }
        internal int CpuThreads { get; }
        internal int CpuBaseMhz { get; }
        internal List<PerformanceDiskInfo> Disks { get; }
        internal List<string> Gpus { get; }
        internal List<string> NetAdapters { get; }

        internal PerformanceHardwareInfo(string cpu, string ram, string gpu, string network,
            double totalRamGb, int cpuCores, int cpuThreads, int cpuBaseMhz,
            List<PerformanceDiskInfo> disks, List<string> gpus, List<string> netAdapters)
        {
            Cpu = cpu;
            Ram = ram;
            Gpu = gpu;
            Network = network;
            TotalRamGb = totalRamGb;
            CpuCores = cpuCores;
            CpuThreads = cpuThreads;
            CpuBaseMhz = cpuBaseMhz;
            Disks = disks;
            Gpus = gpus;
            NetAdapters = netAdapters;
        }

        internal static PerformanceHardwareInfo Empty => new(
            "-", "-", "-", "-", 0, 0, 0, 0, [], [], []);
    }

    internal static class PerformanceHardwareService
    {
        internal static PerformanceHardwareInfo Gather(CancellationToken cancellationToken)
        {
            string cpu = "-", ram = "-", gpu = "-", network = "-";
            double totalRamGb = 0;
            int cores = 0, threads = 0, baseMhz = 0;

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
                using var rows = searcher.Get();
                var names = new List<string>();
                foreach (ManagementObject row in rows.Cast<ManagementObject>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using (row)
                    {
                        string name = (row["Name"] as string ?? string.Empty).Trim();
                        if (name.Length > 0 && !names.Contains(name)) names.Add(name);
                        if (row["NumberOfCores"] is { } coreCount) cores += Convert.ToInt32(coreCount);
                        if (row["NumberOfLogicalProcessors"] is { } threadCount) threads += Convert.ToInt32(threadCount);
                        if (baseMhz == 0 && row["MaxClockSpeed"] is { } speed) baseMhz = Convert.ToInt32(speed);
                    }
                }
                if (names.Count > 0) cpu = string.Join(" + ", names) + $" ({cores}C / {threads}T)";
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");
                using var rows = searcher.Get();
                foreach (ManagementObject row in rows.Cast<ManagementObject>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using (row)
                    {
                        if (row["TotalPhysicalMemory"] is not { } total) continue;
                        totalRamGb = Convert.ToInt64(total) / 1024.0 / 1024.0 / 1024.0;
                        ram = totalRamGb.ToString("0.0", CultureInfo.InvariantCulture) + " GB installed";
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }

            var gpus = new List<string>();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
                using var rows = searcher.Get();
                foreach (ManagementObject row in rows.Cast<ManagementObject>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using (row)
                    {
                        string name = (row["Name"] as string ?? string.Empty).Trim();
                        if (name.Length > 0) gpus.Add(name);
                    }
                }
                if (gpus.Count > 0) gpu = string.Join(", ", gpus);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }

            var models = new Dictionary<int, string>();
            var lettersByDisk = new Dictionary<int, List<string>>();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT Index, Model FROM Win32_DiskDrive");
                using var rows = searcher.Get();
                foreach (ManagementObject row in rows.Cast<ManagementObject>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using (row)
                    {
                        if (row["Index"] is not { } rawIndex) continue;
                        int index = Convert.ToInt32(rawIndex);
                        string model = (row["Model"] as string ?? string.Empty).Trim();
                        if (model.Length > 0) models[index] = model;
                        lettersByDisk[index] = ReadDriveLetters(row, cancellationToken);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }

            var disks = new List<PerformanceDiskInfo>();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (string instance in new PerformanceCounterCategory("PhysicalDisk").GetInstanceNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (instance == "_Total") continue;
                    int separator = instance.IndexOf(' ');
                    bool hasIndex = int.TryParse(separator > 0 ? instance[..separator] : instance, out int index);
                    string model = hasIndex && models.TryGetValue(index, out string? foundModel) ? foundModel : instance;
                    List<string> letters = hasIndex && lettersByDisk.TryGetValue(index, out List<string>? foundLetters)
                        ? foundLetters : [];
                    disks.Add(new PerformanceDiskInfo(instance, model, letters));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }

            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, Speed FROM Win32_NetworkAdapter WHERE NetEnabled = TRUE AND PhysicalAdapter = TRUE");
                using var rows = searcher.Get();
                var adapters = new List<string>();
                foreach (ManagementObject row in rows.Cast<ManagementObject>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using (row)
                    {
                        string name = (row["Name"] as string ?? string.Empty).Trim();
                        if (name.Length == 0) continue;
                        string speed = row["Speed"] is { } rawSpeed
                            ? PerformanceMetricFormatter.LinkSpeed(Convert.ToUInt64(rawSpeed)) : string.Empty;
                        adapters.Add(speed.Length > 0 ? $"{name} - {speed}" : name);
                    }
                }
                if (adapters.Count > 0) network = string.Join("; ", adapters);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }

            var counterAdapters = new List<string>();
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (string instance in new PerformanceCounterCategory("Network Interface").GetInstanceNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (instance.IndexOf("Loopback", StringComparison.OrdinalIgnoreCase) < 0 &&
                        instance.IndexOf("isatap", StringComparison.OrdinalIgnoreCase) < 0)
                        counterAdapters.Add(instance);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }

            return new PerformanceHardwareInfo(cpu, ram, gpu, network, totalRamGb, cores, threads,
                baseMhz, disks, gpus, counterAdapters);
        }

        private static List<string> ReadDriveLetters(
            ManagementObject disk, CancellationToken cancellationToken)
        {
            var letters = new List<string>();
            try
            {
                using var partitions = disk.GetRelated("Win32_DiskPartition");
                foreach (ManagementObject partition in partitions.Cast<ManagementObject>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using (partition)
                    using (var logicalDisks = partition.GetRelated("Win32_LogicalDisk"))
                    foreach (ManagementObject logicalDisk in logicalDisks.Cast<ManagementObject>())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        using (logicalDisk)
                        {
                            string letter = (logicalDisk["DeviceID"] as string ?? string.Empty).Trim();
                            if (letter.Length > 0) letters.Add(letter);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
            letters.Sort(StringComparer.OrdinalIgnoreCase);
            return letters;
        }
    }
}
