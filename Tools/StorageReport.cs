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
        internal List<StorageReportNode> Children { get; } = new();
    }

    internal sealed class StorageReport
    {
        internal StorageReport(string scanRoot, string viewRoot, long totalSize, int depthLimit,
                               long minimumSize, bool colorByFolder, StorageReportNode root)
        {
            ScanRoot = scanRoot; ViewRoot = viewRoot; TotalSize = totalSize;
            DepthLimit = depthLimit; MinimumSize = minimumSize;
            ColorByFolder = colorByFolder; Root = root;
        }

        internal string ScanRoot { get; }
        internal string ViewRoot { get; }
        internal long TotalSize { get; }
        internal int DepthLimit { get; }
        internal long MinimumSize { get; }
        internal bool ColorByFolder { get; }
        internal StorageReportNode Root { get; }
    }
}
