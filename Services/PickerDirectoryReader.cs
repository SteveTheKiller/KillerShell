using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace KillerShell.Services
{
    public enum PickerListingLimit { None, TooManyEntries, TooSlow }

    /// <summary>One folder's rows as the picker read them, and why the read stopped early if it did.</summary>
    public sealed class PickerListing(PickerEntry[] entries, PickerListingLimit limit)
    {
        public PickerEntry[] Entries { get; } = entries;
        public PickerListingLimit Limit { get; } = limit;
    }

    public static class PickerDirectoryReader
    {
        public const int MaximumEntries = 10000;
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

        /// <summary>
        /// The folder's rows, or null when it does not exist. A folder too big or too slow to read
        /// in full still lists what was read, marked with the limit it reached, so the picker can
        /// open it and the user can type the rest.
        /// </summary>
        public static PickerListing? Read(string directory, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory)) return null;
            var entries = new List<PickerEntry>();
            int visited = 0;
            var clock = Stopwatch.StartNew();
            PickerListingLimit Bound()
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++visited > MaximumEntries) return PickerListingLimit.TooManyEntries;
                return clock.Elapsed > TimeSpan.FromSeconds(10) ? PickerListingLimit.TooSlow : PickerListingLimit.None;
            }
            foreach (string path in Directory.EnumerateDirectories(directory))
            {
                if (Bound() is var limit && limit != PickerListingLimit.None) return new PickerListing(entries.ToArray(), limit);
                var info = new DirectoryInfo(path);
                if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                entries.Add(new PickerEntry(info.Name, path, true, 0, SafeTime(() => info.LastWriteTime)));
            }
            foreach (string path in Directory.EnumerateFiles(directory))
            {
                if (Bound() is var limit && limit != PickerListingLimit.None) return new PickerListing(entries.ToArray(), limit);
                var info = new FileInfo(path);
                if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                entries.Add(new PickerEntry(info.Name, path, false, SafeLength(info), SafeTime(() => info.LastWriteTime)));
            }
            return new PickerListing(entries.ToArray(), PickerListingLimit.None);
        }

        /// <summary>Text for a listing that stopped early, in the app's language; null when complete.</summary>
        internal static string? LimitNote(PickerListingLimit limit, Func<string, string> loc) => limit switch
        {
            PickerListingLimit.TooManyEntries => string.Format(loc("Str_Dlg_ListingTooMany"), MaximumEntries),
            PickerListingLimit.TooSlow => loc("Str_Dlg_ListingTooSlow"),
            _ => null
        };

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
