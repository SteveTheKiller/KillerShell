using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace KillerShell.Services
{
    internal static class PickerDirectoryReader
    {
        internal static (string Label, string Path)[] ReadDrives(CancellationToken cancellationToken)
        {
            var places = new List<(string, string)>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!drive.IsReady) continue;
                    string label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.DriveType.ToString() : drive.VolumeLabel.Trim();
                    places.Add(($"{drive.Name.TrimEnd('\\')}  {label}", drive.RootDirectory.FullName));
                }
                catch { /* A disconnected drive does not hide the other places. */ }
            }
            return places.ToArray();
        }

        internal static PickerEntry[]? Read(string directory, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory)) return null;
            var entries = new List<PickerEntry>();
            try
            {
                foreach (string path in Directory.EnumerateDirectories(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var info = new DirectoryInfo(path);
                    if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    entries.Add(new PickerEntry(info.Name, path, true, 0, SafeTime(() => info.LastWriteTime)));
                }
                foreach (string path in Directory.EnumerateFiles(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var info = new FileInfo(path);
                    if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    entries.Add(new PickerEntry(info.Name, path, false, SafeLength(info), SafeTime(() => info.LastWriteTime)));
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Preserve readable entries when a directory becomes inaccessible during enumeration.
            }
            return entries.ToArray();
        }

        private static DateTime SafeTime(Func<DateTime> read)
        {
            try { return read(); } catch { return DateTime.MinValue; }
        }

        private static long SafeLength(FileInfo info)
        {
            try { return info.Length; } catch { return 0; }
        }
    }
}
