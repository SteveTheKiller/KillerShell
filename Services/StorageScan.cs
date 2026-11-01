using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using KillerShell.Tools;

namespace KillerShell.Services
{
    /// <summary>One row of the storage command's top lists.</summary>
    internal readonly struct StorageScanEntry
    {
        internal string Path { get; }
        internal long Size { get; }
        internal StorageScanEntry(string path, long size) { Path = path; Size = size; }
    }

    internal sealed class StorageScanResult
    {
        internal string Root { get; set; } = string.Empty;
        internal long TotalBytes { get; set; }
        internal int Files { get; set; }
        internal int Directories { get; set; }
        internal int SkippedDirectories { get; set; }
        internal bool Truncated { get; set; }
        /// <summary>Files and folders with a nonzero size, before the top lists cut them down.</summary>
        internal int SizedFiles { get; set; }
        internal int SizedFolders { get; set; }
        internal bool LimitReached(int top) => SizedFiles > top || SizedFolders > top;
        internal List<StorageScanEntry> TopFolders { get; } = [];
        internal List<StorageScanEntry> TopFiles { get; } = [];
        internal StorageReportNode? Tree { get; set; }
    }

    /// <summary>
    /// A read-only walk of one folder for the command line (the storage verb). It reads each
    /// folder with StorageDirectoryReader, which skips reparse points so a junction cannot loop,
    /// and never touches the MFT, so it needs no elevation and no volume handle.
    /// </summary>
    internal static class StorageScan
    {
        /// <summary>Upper bound on entries visited, so one command cannot run without end on a huge volume.</summary>
        internal const int MaximumEntries = 3_000_000;

        private const int StackBytes = 64 * 1024 * 1024;

        internal static StorageScanResult Scan(string root, int top, bool buildTree, CancellationToken cancellationToken)
        {
            var result = new StorageScanResult { Root = root };
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var walker = new Walker(result, top, buildTree, cancellationToken);
                    var node = walker.Walk(root, root, isRoot: true);
                    result.TotalBytes = node.Size;
                    if (buildTree) { Prune(node, node.Size / 5000); result.Tree = node; }
                }
                catch (Exception ex) { failure = ex; }
            }, StackBytes);
            thread.Start();
            thread.Join();
            if (failure != null) throw failure;
            return result;
        }

        private static void Prune(StorageReportNode node, long minimum)
        {
            node.Children.RemoveAll(child => child.Size < minimum);
            foreach (var child in node.Children) Prune(child, minimum);
        }

        private sealed class Walker
        {
            private readonly StorageScanResult _result;
            private readonly int _top;
            private readonly bool _tree;
            private readonly CancellationToken _token;
            private int _visited;

            internal Walker(StorageScanResult result, int top, bool tree, CancellationToken token)
            {
                _result = result; _top = top; _tree = tree; _token = token;
            }

            internal StorageReportNode Walk(string path, string name, bool isRoot)
            {
                var children = new List<StorageReportNode>();
                long total = 0;
                var read = StorageDirectoryReader.Read(path, _token);
                if (read.Skipped) _result.SkippedDirectories++;
                else if (!isRoot) _result.Directories++;
                foreach (var entry in read.Entries)
                {
                    if (_token.IsCancellationRequested) { _result.Truncated = true; break; }
                    if (++_visited > MaximumEntries) { _result.Truncated = true; break; }
                    string child = path.TrimEnd('\\') + "\\" + entry.Name;
                    if (entry.IsDirectory)
                    {
                        var sub = Walk(child, entry.Name, isRoot: false);
                        total += sub.Size;
                        if (_tree) children.Add(sub);
                    }
                    else
                    {
                        _result.Files++;
                        total += entry.Size;
                        if (entry.Size > 0) _result.SizedFiles++;
                        Track(_result.TopFiles, child, entry.Size);
                        if (_tree) children.Add(new StorageReportNode(entry.Name, child, entry.Size, false));
                    }
                }
                if (!isRoot)
                {
                    if (total > 0) _result.SizedFolders++;
                    Track(_result.TopFolders, path, total);
                }
                var node = new StorageReportNode(name, path, total, true);
                children.Sort((left, right) => right.Size.CompareTo(left.Size));
                node.Children.AddRange(children);
                return node;
            }

            // Keeps the list sorted largest first and no longer than the limit.
            private void Track(List<StorageScanEntry> list, string path, long size)
            {
                if (size <= 0) return;
                if (list.Count >= _top && size <= list[list.Count - 1].Size) return;
                int at = 0;
                while (at < list.Count && list[at].Size >= size) at++;
                list.Insert(at, new StorageScanEntry(path, size));
                if (list.Count > _top) list.RemoveAt(list.Count - 1);
            }
        }
    }
}
