using System.Collections.Generic;

namespace KillerShell.Tools
{
    internal sealed class StorageReportNode
    {
        internal StorageReportNode(string name, string path, long size, bool isDirectory)
        {
            Name = name; Path = path; Size = size; IsDirectory = isDirectory;
        }

        internal string Name { get; }
        internal string Path { get; }
        internal long Size { get; }
        internal bool IsDirectory { get; }
        internal List<StorageReportNode> Children { get; } = [];
    }

    internal sealed class StorageReport
    {
        internal StorageReport(string scanRoot, string viewRoot, long totalSize, int depthLimit,
                               long minimumSize, bool colorByFolder, StorageReportNode root,
                               bool truncated = false, int skippedDirectories = 0)
        {
            ScanRoot = scanRoot; ViewRoot = viewRoot; TotalSize = totalSize;
            DepthLimit = depthLimit; MinimumSize = minimumSize;
            ColorByFolder = colorByFolder; Root = root;
            Truncated = truncated; SkippedDirectories = skippedDirectories;
        }

        /// <summary>The scan stopped early (a time limit, the entry cap or a cancel), so the figures are partial.</summary>
        internal bool Truncated { get; }
        /// <summary>Folders the scan could not read and left out.</summary>
        internal int SkippedDirectories { get; }

        internal string ScanRoot { get; }
        internal string ViewRoot { get; }
        internal long TotalSize { get; }
        internal int DepthLimit { get; }
        internal long MinimumSize { get; }
        internal bool ColorByFolder { get; }
        internal StorageReportNode Root { get; }
    }
}
