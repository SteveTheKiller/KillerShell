using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KillerShell.Services
{
    /// <summary>Small archive listings and text samples held entirely in memory.</summary>
    internal static class DemoArchives
    {
        internal static bool Contains(string archivePath)
            => ArchiveProvider.IsReadable(archivePath) && DemoFs.Find(archivePath) is { IsDir: false };

        internal static List<ArchiveEntryInfo> List(string archivePath, string entryPath)
        {
            var rows = new Dictionary<string, ArchiveEntryInfo>(StringComparer.Ordinal);
            if (!Contains(archivePath)) return [];
            string prefix = entryPath.Length == 0 ? string.Empty : entryPath.Trim('/') + "/";
            DateTime modified = DemoFs.Find(archivePath)!.Modified;
            foreach (var file in Files(archivePath))
            {
                if (!file.Path.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string remaining = file.Path.Substring(prefix.Length);
                if (remaining.Length == 0) continue;
                int slash = remaining.IndexOf('/');
                string name = slash < 0 ? remaining : remaining.Substring(0, slash);
                if (!rows.TryGetValue(name, out var row))
                {
                    row = new ArchiveEntryInfo
                    {
                        Name = name, EntryPath = prefix + name, IsDirectory = slash >= 0, Modified = modified,
                    };
                    rows.Add(name, row);
                }
                row.Size += Encoding.UTF8.GetByteCount(file.Text);
            }
            return rows.Values.OrderByDescending(row => row.IsDirectory)
                .ThenBy(row => row.Name, StringComparer.Ordinal).ToList();
        }

        internal static string? TextFor(string virtualPath)
        {
            if (!ArchiveProvider.TrySplit(virtualPath, out string archive, out string entry)
                || entry.Length == 0 || !Contains(archive)) return null;
            foreach (var file in Files(archive))
                if (string.Equals(file.Path, entry, StringComparison.Ordinal)) return file.Text;
            return null;
        }

        private static List<(string Path, string Text)> Files(string archivePath)
        {
            string name = Path.GetFileName(archivePath);
            var files = new List<(string Path, string Text)>
            {
                ("README.md", "# " + name + "\r\n\r\n"
                    + "Sample archive from the Fairview field workstation.\r\n\r\n"
                    + "Open a folder to browse its contents, then open a file to edit the sample text.\r\n"
                    + "Saved text stays available for this demo session. The archive is never written to disk.\r\n"),
            };
            if (name.IndexOf("photo", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                files.Add(("Site Notes/photo-index.csv", "File,Location,Description\r\n"
                    + "rack-front.jpg,Fairview,Main network rack\r\n"
                    + "switch-labels.jpg,Fairview,Access switch port labels\r\n"
                    + "ups-panel.jpg,Cedar Ridge,UPS status panel\r\n"));
                files.Add(("Site Notes/checklist.md", "# Site photo checklist\r\n\r\n"
                    + "- [x] Rack overview\r\n- [x] Cable and port labels\r\n- [x] UPS status\r\n"
                    + "- [ ] Add the updated floor plan\r\n"));
            }
            else if (name.IndexOf("invoice", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("contract", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                files.Add(("Records/register.csv", "Reference,Customer,Status\r\n"
                    + "FV-2025-101,Fairview,Approved\r\nCR-2025-204,Cedar Ridge,Reviewed\r\n"));
                files.Add(("Records/notes.md", "# Archive notes\r\n\r\n"
                    + "The sample records are grouped by customer and reference number.\r\n"
                    + "Review the register before preparing the next billing export.\r\n"));
            }
            else if (name.IndexOf("driver", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("veeam", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("firmware", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                files.Add(("Package/install-settings.json", "{\r\n  \"site\": \"Fairview\",\r\n"
                    + "  \"channel\": \"stable\",\r\n  \"restartAutomatically\": false\r\n}\r\n"));
                files.Add(("Package/verification.log", "2026-06-25 09:10:00 INFO Package inventory checked\r\n"
                    + "2026-06-25 09:10:01 INFO Test workstation ready\r\n"
                    + "2026-06-25 09:10:02 INFO No installation started in demo mode\r\n"));
            }
            else
            {
                files.Add(("Reports/summary.log", "2026-05-31 22:00:00 INFO Scheduled job started\r\n"
                    + "2026-05-31 22:04:18 INFO 142 devices checked\r\n"
                    + "2026-05-31 22:04:19 INFO Report archived successfully\r\n"));
                files.Add(("Reports/status.csv", "Site,Devices,Status\r\nFairview,86,Complete\r\nCedar Ridge,56,Complete\r\n"));
            }
            return files;
        }
    }
}
