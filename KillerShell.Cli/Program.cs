using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using KillerShell.Models;
using KillerShell.Services;
using Microsoft.Win32;

namespace KillerShell.Cli
{
    internal static class Program
    {
        private const int DefaultLimit = 100;
        private const int MaximumLimit = 500;
        private const int DefaultReadCharacters = 32768;
        private const int MaximumReadCharacters = 65536;
        private const int MaximumEventScan = 20000;

        internal static int Main(string[] args)
        {
            if (args.Length > 1 && args[args.Length - 1] == "--json")
                args = args.Take(args.Length - 1).ToArray();
            try { Console.OutputEncoding = new UTF8Encoding(false); }
            catch (IOException) { /* A GUI process with redirected output can lack a console handle. */ }
            if (args.Length == 1 && (args[0] == "--version" || args[0] == "-v" || args[0] == "/version" || args[0] == "version"))
            {
                Console.WriteLine(typeof(SearchEngine).Assembly.GetName().Version);
                return 0;
            }
            if (args.Length == 1 && args[0] == "--help")
            {
                PrintUsage();
                return 0;
            }
            if (args.Length == 0)
            {
                PrintUsage();
                return 2;
            }

            try
            {
                switch (args[0])
                {
                    case "search": return Search(args);
                    case "list": return ListDirectory(args);
                    case "info": return FileInfo(args);
                    case "read": return ReadText(args);
                    case "processes": return Processes(args);
                    case "services": return Services(args);
                    case "events": return Events(args);
                    case "registry": return Registry(args);
                    case "drives": return Drives(args);
                    case "hash": return HashFile(args);
                    case "storage": return Storage(args);
                    default:
                        PrintUsage();
                        return 2;
                }
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        private static int ListDirectory(string[] args)
        {
            if (args.Length < 2) throw new ArgumentException("List needs one directory path");
            string root = Path.GetFullPath(args[1]);
            if (!Directory.Exists(root))
                throw new ArgumentException("List path is not a directory");

            int limit = ReadSingleIntegerOption(args, "--limit", DefaultLimit, 1, MaximumLimit);
            var entries = new DirectoryInfo(root).EnumerateFileSystemInfos()
                .OrderByDescending(item => (item.Attributes & FileAttributes.Directory) != 0)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Take(limit + 1)
                .ToList();
            bool limitReached = entries.Count > limit;
            if (limitReached) entries.RemoveAt(entries.Count - 1);

            var output = new StringBuilder("{\"path\":").Append(JsonString(root)).Append(",\"entries\":[");
            bool first = true;
            foreach (var item in entries)
            {
                if (!first) output.Append(',');
                first = false;
                bool isDirectory = (item.Attributes & FileAttributes.Directory) != 0;
                output.Append("{\"name\":").Append(JsonString(item.Name));
                output.Append(",\"path\":").Append(JsonString(item.FullName));
                output.Append(",\"isDirectory\":").Append(isDirectory ? "true" : "false");
                output.Append(",\"sizeBytes\":");
                if (isDirectory) output.Append("null");
                else output.Append(((System.IO.FileInfo)item).Length.ToString(CultureInfo.InvariantCulture));
                output.Append(",\"modifiedUtc\":").Append(JsonString(item.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture)));
                output.Append('}');
            }
            output.Append("],\"limitReached\":").Append(limitReached ? "true" : "false").Append('}');
            Console.WriteLine(output.ToString());
            return 0;
        }

        private static int FileInfo(string[] args)
        {
            if (args.Length != 2)
                throw new ArgumentException("File details accept one path");

            string path = Path.GetFullPath(args[1]);
            bool isDirectory = Directory.Exists(path);
            if (!isDirectory && !File.Exists(path))
                throw new ArgumentException("Path does not exist");

            FileSystemInfo item = isDirectory ? (FileSystemInfo)new DirectoryInfo(path) : new System.IO.FileInfo(path);
            var output = new StringBuilder("{\"name\":").Append(JsonString(item.Name));
            output.Append(",\"path\":").Append(JsonString(item.FullName));
            output.Append(",\"isDirectory\":").Append(isDirectory ? "true" : "false");
            output.Append(",\"sizeBytes\":");
            if (isDirectory) output.Append("null");
            else output.Append(((System.IO.FileInfo)item).Length.ToString(CultureInfo.InvariantCulture));
            output.Append(",\"createdUtc\":").Append(JsonString(item.CreationTimeUtc.ToString("O", CultureInfo.InvariantCulture)));
            output.Append(",\"modifiedUtc\":").Append(JsonString(item.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture)));
            output.Append(",\"attributes\":").Append(JsonString(item.Attributes.ToString())).Append('}');
            Console.WriteLine(output.ToString());
            return 0;
        }

        private static int ReadText(string[] args)
        {
            if (args.Length < 2) throw new ArgumentException("Read needs one file path");
            string path = Path.GetFullPath(args[1]);
            if (!File.Exists(path))
                throw new ArgumentException("Read path is not a file");

            int maximum = ReadSingleIntegerOption(args, "--max-chars", DefaultReadCharacters, 1, MaximumReadCharacters);
            var buffer = new char[maximum + 1];
            int count;
            using (var reader = new StreamReader(path, Encoding.UTF8, true))
                count = reader.ReadBlock(buffer, 0, buffer.Length);
            int keep = Math.Min(count, maximum);
            // Never end on half of a surrogate pair: the lone half is not valid UTF-8 output.
            if (count > maximum && keep > 0 && char.IsHighSurrogate(buffer[keep - 1])) keep--;
            string text = new(buffer, 0, keep);
            if (text.IndexOf('\0') >= 0)
                throw new ArgumentException("File does not appear to contain text");

            var info = new System.IO.FileInfo(path);
            var output = new StringBuilder("{\"path\":").Append(JsonString(path));
            output.Append(",\"sizeBytes\":").Append(info.Length.ToString(CultureInfo.InvariantCulture));
            output.Append(",\"text\":").Append(JsonString(text));
            output.Append(",\"truncated\":").Append(count > maximum ? "true" : "false").Append('}');
            Console.WriteLine(output.ToString());
            return 0;
        }

        private static int Processes(string[] args)
        {
            var options = ParseOptions(args, 1, ["--limit", "--filter"], ["--detail"]);
            int limit = LimitOption(options, DefaultLimit, 1, MaximumLimit);
            bool detail = options.Flags.Contains("--detail");
            string? filter = options.Values.TryGetValue("--filter", out var filterText) ? filterText : null;
            // The WMI read is the slow part, so it runs only when a detail column or the filter needs it.
            var details = detail ? ProcessListQueryService.QueryProcesses()
                : new Dictionary<int, (string Name, string CommandLine, string Path, string ParentPid, long? CreatedUtcTicks)>();
            var all = Process.GetProcesses().OrderBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Id).ToList();
            var rows = new List<Process>();
            foreach (var candidate in all)
            {
                bool keep = rows.Count <= limit && (filter == null
                    || Contains(candidate.ProcessName, filter)
                    || candidate.Id.ToString(CultureInfo.InvariantCulture).Equals(filter, StringComparison.Ordinal)
                    || (detail && details.TryGetValue(candidate.Id, out var info)
                        && (Contains(info.CommandLine, filter) || Contains(info.Path, filter))));
                if (keep) rows.Add(candidate); else candidate.Dispose();
            }
            bool limitReached = rows.Count > limit;
            if (limitReached) { rows[rows.Count - 1].Dispose(); rows.RemoveAt(rows.Count - 1); }
            var output = new StringBuilder("{\"processes\":[");
            for (int i = 0; i < rows.Count; i++)
            {
                using var process = rows[i];
                if (i > 0) output.Append(',');
                output.Append("{\"name\":").Append(JsonString(process.ProcessName));
                output.Append(",\"pid\":").Append(process.Id.ToString(CultureInfo.InvariantCulture));
                try { output.Append(",\"memoryBytes\":").Append(process.WorkingSet64.ToString(CultureInfo.InvariantCulture)); }
                catch { output.Append(",\"memoryBytes\":null"); }
                if (detail)
                {
                    details.TryGetValue(process.Id, out var info);
                    output.Append(",\"parentPid\":").Append(int.TryParse(info.ParentPid, NumberStyles.None, CultureInfo.InvariantCulture, out int parent)
                        ? parent.ToString(CultureInfo.InvariantCulture) : "null");
                    output.Append(",\"path\":").Append(JsonString(info.Path ?? string.Empty));
                    output.Append(",\"commandLine\":").Append(JsonString(info.CommandLine ?? string.Empty));
                    output.Append(",\"startedUtc\":").Append(info.CreatedUtcTicks.HasValue
                        ? JsonString(new DateTime(info.CreatedUtcTicks.Value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture)) : "null");
                }
                output.Append('}');
            }
            output.Append("],\"limitReached\":").Append(limitReached ? "true" : "false").Append('}');
            Console.WriteLine(output.ToString());
            return 0;
        }

        private static int Services(string[] args)
        {
            var options = ParseOptions(args, 1, ["--limit", "--filter"], ["--detail"]);
            int limit = LimitOption(options, DefaultLimit, 1, MaximumLimit);
            bool detail = options.Flags.Contains("--detail");
            string? filter = options.Values.TryGetValue("--filter", out var filterText) ? filterText : null;
            var details = detail ? ProcessListQueryService.QueryServices()
                : new Dictionary<string, (string StartMode, string Path, string LogOnAs, string Description)>();
            using var services = new DisposableList<ServiceController>(ServiceController.GetServices());
            var rows = services.Items.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Where(item => filter == null || Contains(item.ServiceName, filter) || Contains(item.DisplayName, filter))
                .Take(limit + 1).ToList();
            bool limitReached = rows.Count > limit;
            if (limitReached) rows.RemoveAt(rows.Count - 1);
            var output = new StringBuilder("{\"services\":[");
            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0) output.Append(',');
                var service = rows[i];
                output.Append("{\"name\":").Append(JsonString(service.ServiceName));
                output.Append(",\"displayName\":").Append(JsonString(service.DisplayName));
                output.Append(",\"status\":").Append(JsonString(service.Status.ToString()));
                if (detail)
                {
                    details.TryGetValue(service.ServiceName, out var info);
                    output.Append(",\"startMode\":").Append(JsonString(info.StartMode ?? string.Empty));
                    output.Append(",\"path\":").Append(JsonString(info.Path ?? string.Empty));
                    output.Append(",\"logOnAs\":").Append(JsonString(info.LogOnAs ?? string.Empty));
                    output.Append(",\"description\":").Append(JsonString(info.Description ?? string.Empty));
                }
                output.Append('}');
            }
            output.Append("],\"limitReached\":").Append(limitReached ? "true" : "false").Append('}');
            Console.WriteLine(output.ToString());
            return 0;
        }

        private static int Events(string[] args)
        {
            if (args.Length < 2) throw new ArgumentException("Events need a log name and optional limit");
            string log = args[1];
            if (log != "Application" && log != "System" && log != "Security")
                throw new ArgumentException("Log must be Application, System, or Security");
            var options = ParseOptions(args, 2, ["--limit", "--filter", "--level"], []);
            int limit = LimitOption(options, 50, 1, 100);
            string? filter = options.Values.TryGetValue("--filter", out var filterText) ? filterText : null;
            string queryText = "*";
            if (options.Values.TryGetValue("--level", out var levelText))
            {
                int level = levelText.ToLowerInvariant() switch
                {
                    "critical" => 1,
                    "error" => 2,
                    "warning" => 3,
                    "information" or "info" => 4,
                    "verbose" => 5,
                    _ => throw new ArgumentException("--level must be Critical, Error, Warning, Information, or Verbose"),
                };
                queryText = "*[System[(Level=" + level.ToString(CultureInfo.InvariantCulture) + ")]]";
            }
            var query = new EventLogQuery(log, PathType.LogName, queryText) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            var output = new StringBuilder("{\"log\":").Append(JsonString(log)).Append(",\"events\":[");
            int count = 0;
            int scanned = 0;
            while (count < limit && scanned < MaximumEventScan)
            {
                using EventRecord record = reader.ReadEvent();
                if (record == null) break;
                scanned++;
                string message;
                try { message = record.FormatDescription() ?? string.Empty; } catch { message = string.Empty; }
                if (filter != null && !(Contains(record.ProviderName, filter) || Contains(message, filter)
                    || record.Id.ToString(CultureInfo.InvariantCulture).Equals(filter, StringComparison.Ordinal)))
                    continue;
                if (count > 0) output.Append(',');
                output.Append("{\"id\":").Append(record.Id.ToString(CultureInfo.InvariantCulture));
                output.Append(",\"level\":").Append(JsonString(record.LevelDisplayName ?? string.Empty));
                output.Append(",\"provider\":").Append(JsonString(record.ProviderName ?? string.Empty));
                output.Append(",\"timeUtc\":").Append(record.TimeCreated.HasValue ? JsonString(record.TimeCreated.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)) : "null");
                output.Append(",\"message\":").Append(JsonString(message.Length <= 2000 ? message : message[..2000])).Append('}');
                count++;
            }
            output.Append("],\"limitReached\":").Append(count == limit ? "true" : "false");
            output.Append(",\"scanLimitReached\":").Append(count < limit && scanned >= MaximumEventScan ? "true" : "false").Append('}');
            Console.WriteLine(output.ToString());
            return 0;
        }

        private static int Registry(string[] args)
        {
            if (args.Length < 2 || args.Length > 4) throw new ArgumentException("Registry inspection needs one key path and optional limit");
            int limit = ReadSingleIntegerOption(args, "--limit", DefaultLimit, 1, 100, 2);
            using var key = OpenRegistryKey(args[1]) ?? throw new ArgumentException("Registry key does not exist or cannot be read");
            var subkeys = key.GetSubKeyNames().OrderBy(value => value, StringComparer.OrdinalIgnoreCase).Take(limit).ToArray();
            var names = key.GetValueNames().OrderBy(value => value, StringComparer.OrdinalIgnoreCase).Take(limit).ToArray();
            var output = new StringBuilder("{\"path\":").Append(JsonString(args[1])).Append(",\"subkeys\":[");
            for (int i = 0; i < subkeys.Length; i++) { if (i > 0) output.Append(','); output.Append(JsonString(subkeys[i])); }
            output.Append("],\"values\":[");
            for (int i = 0; i < names.Length; i++)
            {
                if (i > 0) output.Append(',');
                string name = names[i];
                RegistryValueKind kind = key.GetValueKind(name);
                object value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                output.Append("{\"name\":").Append(JsonString(name));
                output.Append(",\"kind\":").Append(JsonString(kind.ToString()));
                output.Append(",\"data\":").Append(JsonString(RegistryData(value))).Append('}');
            }
            bool limited = key.SubKeyCount > subkeys.Length || key.ValueCount > names.Length;
            output.Append("],\"limitReached\":").Append(limited ? "true" : "false").Append('}');
            Console.WriteLine(output.ToString());
            return 0;
        }

        private static int Drives(string[] args)
        {
            if (args.Length != 1) throw new ArgumentException("Drives does not accept arguments");
            var output = new StringBuilder("{\"drives\":[");
            bool first = true;
            foreach (var drive in DriveInfo.GetDrives().OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!first) output.Append(',');
                first = false;
                output.Append("{\"name\":").Append(JsonString(drive.Name));
                output.Append(",\"type\":").Append(JsonString(drive.DriveType.ToString()));
                output.Append(",\"ready\":").Append(drive.IsReady ? "true" : "false");
                if (drive.IsReady)
                {
                    output.Append(",\"format\":").Append(JsonString(drive.DriveFormat));
                    output.Append(",\"totalBytes\":").Append(drive.TotalSize.ToString(CultureInfo.InvariantCulture));
                    output.Append(",\"freeBytes\":").Append(drive.AvailableFreeSpace.ToString(CultureInfo.InvariantCulture));
                }
                output.Append('}');
            }
            Console.WriteLine(output.Append("]}").ToString());
            return 0;
        }

        private static int HashFile(string[] args)
        {
            if (args.Length != 2) throw new ArgumentException("Hash accepts one file path");
            string path = Path.GetFullPath(args[1]);
            if (!File.Exists(path)) throw new ArgumentException("Hash path is not a file");
            using var stream = File.OpenRead(path);
            using var algorithm = SHA256.Create();
            string hash = BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            Console.WriteLine("{\"path\":" + JsonString(path) + ",\"algorithm\":\"SHA-256\",\"hash\":" + JsonString(hash) + "}");
            return 0;
        }

        private static RegistryKey OpenRegistryKey(string path)
        {
            int separator = path.IndexOf('\\');
            string hive = separator < 0 ? path : path[..separator];
            string subkey = separator < 0 ? string.Empty : path[(separator + 1)..];
            RegistryKey root = hive.ToUpperInvariant() switch
            {
                "HKEY_CLASSES_ROOT" or "HKCR" => Microsoft.Win32.Registry.ClassesRoot,
                "HKEY_CURRENT_USER" or "HKCU" => Microsoft.Win32.Registry.CurrentUser,
                "HKEY_LOCAL_MACHINE" or "HKLM" => Microsoft.Win32.Registry.LocalMachine,
                "HKEY_USERS" or "HKU" => Microsoft.Win32.Registry.Users,
                "HKEY_CURRENT_CONFIG" or "HKCC" => Microsoft.Win32.Registry.CurrentConfig,
                _ => throw new ArgumentException("Registry path must begin with a supported hive name"),
            };
            return subkey.Length == 0 ? root : root.OpenSubKey(subkey, false);
        }

        private static string RegistryData(object value)
        {
            string text = value switch
            {
                null => string.Empty,
                byte[] bytes => BitConverter.ToString(bytes).Replace("-", " "),
                string[] strings => string.Join(" | ", strings),
                _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
            };
            return text.Length <= 4000 ? text : text[..4000];
        }

        private static int ReadSingleIntegerOption(string[] args, string option, int defaultValue, int minimum, int maximum, int valueCount = 2)
        {
            if (args.Length == valueCount) return defaultValue;
            if (args.Length != valueCount + 2 || args[valueCount] != option
                || !int.TryParse(args[valueCount + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                || value < minimum || value > maximum)
                throw new ArgumentException(option + " must be between " + minimum.ToString(CultureInfo.InvariantCulture)
                    + " and " + maximum.ToString(CultureInfo.InvariantCulture));
            return value;
        }

        private static int Search(string[] args)
        {
            if (args.Length < 2) throw new ArgumentException("Search needs one directory path");
            string root = Path.GetFullPath(args[1]);
            if (!Directory.Exists(root))
                throw new ArgumentException("Search root is not a directory");

            string? name = null;
            string? content = null;
            string? csvPath = null;
            string? htmlPath = null;
            int limit = DefaultLimit;
            for (int i = 2; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length)
                    throw new ArgumentException("Each search option needs a value");
                switch (args[i])
                {
                    case "--name": name = args[i + 1]; break;
                    case "--content": content = args[i + 1]; break;
                    case "--limit":
                        if (!int.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out limit)
                            || limit < 1 || limit > MaximumLimit)
                            throw new ArgumentException("Limit must be between 1 and 500");
                        break;
                    case "--csv": csvPath = ValidateNewOutput(args[i + 1], csvPath, "--csv"); break;
                    case "--html": htmlPath = ValidateNewOutput(args[i + 1], htmlPath, "--html"); break;
                    default: throw new ArgumentException("Unknown search option: " + args[i]);
                }
            }
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(content))
                throw new ArgumentException("Provide --name or --content");
            if (csvPath != null && htmlPath != null && string.Equals(csvPath, htmlPath, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("--csv and --html need different files");

            var group = new TermGroup { Mode = TermGroup.GroupMode.And };
            if (!string.IsNullOrWhiteSpace(name))
                group.Terms.Add(new SearchTerm { Mode = SearchTerm.SearchMode.FileName, Pattern = name! });
            if (!string.IsNullOrWhiteSpace(content))
                group.Terms.Add(new SearchTerm { Mode = SearchTerm.SearchMode.Content, Pattern = content! });

            var found = new List<SearchResult>();
            using var cancellation = new CancellationTokenSource();
            var engine = new SearchEngine();
            engine.ResultsBatch += batch =>
            {
                foreach (var item in batch)
                {
                    if (found.Count >= limit) break;
                    found.Add(item);
                }
                if (found.Count >= limit) cancellation.Cancel();
            };
            System.Threading.Tasks.Task.Run(() => engine.SearchAsync(root, [group], Array.Empty<SearchFilter>(),
                string.Empty, string.Empty, false, cancellation.Token)).GetAwaiter().GetResult();

            var ordered = found.OrderBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase).ToList();
            if (csvPath != null)
            {
                byte[] bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(CsvExport.Build(ordered))).ToArray();
                using var stream = CreateNewOutput(csvPath);
                stream.Write(bytes, 0, bytes.Length);
            }
            if (htmlPath != null)
            {
                CreateNewOutput(htmlPath).Dispose();   // claims the name; the exporter then fills it
                HtmlExporter.Export(htmlPath, ordered, group.Terms.ToList(), root);
            }

            var output = new StringBuilder("{\"results\":[");
            bool first = true;
            foreach (var item in ordered)
            {
                if (!first) output.Append(',');
                first = false;
                output.Append("{\"path\":").Append(JsonString(item.FilePath));
                output.Append(",\"sizeBytes\":").Append(item.SizeBytes.ToString(CultureInfo.InvariantCulture));
                output.Append(",\"modifiedUtc\":").Append(JsonString(item.Modified.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
                output.Append('}');
            }
            output.Append("],\"limitReached\":").Append(cancellation.IsCancellationRequested ? "true" : "false");
            output.Append(",\"incompletePaths\":").Append(engine.IncompletePaths.ToString(CultureInfo.InvariantCulture));
            output.Append(",\"skippedDirectoryLinks\":").Append(engine.SkippedDirectoryLinks.ToString(CultureInfo.InvariantCulture));
            if (csvPath != null) output.Append(",\"csv\":").Append(JsonString(csvPath));
            if (htmlPath != null) output.Append(",\"html\":").Append(JsonString(htmlPath));
            Console.WriteLine(output.Append('}').ToString());
            return 0;
        }

        private static int Storage(string[] args)
        {
            if (args.Length < 2) throw new ArgumentException("Storage needs one directory path");
            string root = Path.GetFullPath(args[1]);
            if (!Directory.Exists(root))
                throw new ArgumentException("Storage path is not a directory");
            var options = ParseOptions(args, 2, ["--limit", "--html"], []);
            int limit = LimitOption(options, 20, 1, MaximumLimit);
            string? htmlPath = options.Values.TryGetValue("--html", out var htmlText) ? ValidateNewOutput(htmlText, null, "--html") : null;

            var scan = StorageScan.Scan(root, limit, htmlPath != null, CancellationToken.None);
            if (htmlPath != null)
            {
                CreateNewOutput(htmlPath).Dispose();   // claims the name; the exporter then fills it
                StorageHtmlExporter.Export(htmlPath, new KillerShell.Tools.StorageReport(root, root, scan.TotalBytes, 0, 0, false,
                    scan.Tree ?? new KillerShell.Tools.StorageReportNode(root, root, scan.TotalBytes, true)));
            }

            var output = new StringBuilder("{\"path\":").Append(JsonString(root));
            output.Append(",\"totalBytes\":").Append(scan.TotalBytes.ToString(CultureInfo.InvariantCulture));
            output.Append(",\"files\":").Append(scan.Files.ToString(CultureInfo.InvariantCulture));
            output.Append(",\"directories\":").Append(scan.Directories.ToString(CultureInfo.InvariantCulture));
            output.Append(",\"skippedDirectories\":").Append(scan.SkippedDirectories.ToString(CultureInfo.InvariantCulture));
            AppendStorageList(output, "topFolders", scan.TopFolders, scan.TotalBytes, false);
            AppendStorageList(output, "topFiles", scan.TopFiles, scan.TotalBytes, true);
            output.Append(",\"limitReached\":").Append(scan.LimitReached(limit) ? "true" : "false");
            output.Append(",\"truncated\":").Append(scan.Truncated ? "true" : "false");
            if (htmlPath != null) output.Append(",\"html\":").Append(JsonString(htmlPath));
            Console.WriteLine(output.Append('}').ToString());
            return 0;
        }

        private static void AppendStorageList(StringBuilder output, string name, List<StorageScanEntry> entries, long total, bool files)
        {
            output.Append(",\"").Append(name).Append("\":[");
            for (int i = 0; i < entries.Count; i++)
            {
                if (i > 0) output.Append(',');
                output.Append("{\"path\":").Append(JsonString(entries[i].Path));
                output.Append(",\"sizeBytes\":").Append(entries[i].Size.ToString(CultureInfo.InvariantCulture));
                output.Append(",\"size\":").Append(JsonString(StorageAnalysisLogic.FormatSize(entries[i].Size)));
                output.Append(",\"percent\":").Append((total > 0 ? 100.0 * entries[i].Size / total : 0).ToString("0.0", CultureInfo.InvariantCulture));
                if (files) output.Append(",\"category\":").Append(JsonString(StorageAnalysisLogic.CategoryFor(Path.GetFileName(entries[i].Path))));
                output.Append('}');
            }
            output.Append(']');
        }

        /// <summary>An export target must be a new file in an existing folder, so a command can
        /// never replace or change something already on disk. A failure is a usage error (exit 2).</summary>
        private static string ValidateNewOutput(string value, string? already, string option)
        {
            if (already != null) throw new ArgumentException(option + " was given twice");
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException(option + " needs a file path");
            string full = Path.GetFullPath(value);
            if (File.Exists(full) || Directory.Exists(full))
                throw new ArgumentException(option + " file already exists; choose a path that does not exist");
            string? folder = Path.GetDirectoryName(full);
            if (folder == null || !Directory.Exists(folder))
                throw new ArgumentException(option + " folder does not exist");
            return full;
        }

        // CreateNew fails if anything appeared at the path since the check above.
        private static FileStream CreateNewOutput(string path)
        {
            try { return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
            catch (IOException) when (File.Exists(path)) { throw new ArgumentException("Output file already exists"); }
        }

        private static bool Contains(string? value, string text)
            => value != null && value.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;

        private sealed class Options
        {
            internal Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
            internal HashSet<string> Flags { get; } = new(StringComparer.Ordinal);
        }

        private static Options ParseOptions(string[] args, int start, string[] valueOptions, string[] flagOptions)
        {
            var options = new Options();
            for (int i = start; i < args.Length; i++)
            {
                string arg = args[i];
                if (Array.IndexOf(flagOptions, arg) >= 0)
                {
                    if (!options.Flags.Add(arg)) throw new ArgumentException(arg + " was given twice");
                }
                else if (Array.IndexOf(valueOptions, arg) >= 0)
                {
                    if (i + 1 >= args.Length) throw new ArgumentException(arg + " needs a value");
                    if (options.Values.ContainsKey(arg)) throw new ArgumentException(arg + " was given twice");
                    options.Values[arg] = args[++i];
                }
                else throw new ArgumentException("Unknown option: " + arg);
            }
            return options;
        }

        private static int LimitOption(Options options, int defaultValue, int minimum, int maximum)
        {
            if (!options.Values.TryGetValue("--limit", out var text)) return defaultValue;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < minimum || value > maximum)
                throw new ArgumentException("--limit must be between " + minimum.ToString(CultureInfo.InvariantCulture)
                    + " and " + maximum.ToString(CultureInfo.InvariantCulture));
            return value;
        }

        private static string JsonString(string value)
        {
            var output = new StringBuilder("\"");
            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                if (character == '"' || character == '\\') output.Append('\\').Append(character);
                else if (character < 0x20) output.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                else if (char.IsHighSurrogate(character) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                    output.Append(character).Append(value[++i]);
                // A lone surrogate (legal in NTFS names and registry strings) has no UTF-8 form,
                // so it goes out as a \u escape instead of a replacement character.
                else if (char.IsSurrogate(character)) output.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                else output.Append(character);
            }
            return output.Append('"').ToString();
        }

        private static void PrintUsage()
        {
            Console.WriteLine("killershell search <folder> --name <pattern> [--content <text>] [--limit 1..500] [--csv <new file>] [--html <new file>]");
            Console.WriteLine("killershell search <folder> --content <text> [--limit 1..500] [--csv <new file>] [--html <new file>]");
            Console.WriteLine("killershell list <folder> [--limit 1..500]");
            Console.WriteLine("killershell info <path>");
            Console.WriteLine("killershell read <file> [--max-chars 1..65536]");
            Console.WriteLine("killershell processes [--limit 1..500] [--detail] [--filter <text>]");
            Console.WriteLine("killershell services [--limit 1..500] [--detail] [--filter <text>]");
            Console.WriteLine("killershell events <Application|System|Security> [--limit 1..100] [--filter <text>] [--level Critical|Error|Warning|Information|Verbose]");
            Console.WriteLine("killershell registry <hive\\key> [--limit 1..100]");
            Console.WriteLine("killershell drives");
            Console.WriteLine("killershell hash <file>");
            Console.WriteLine("killershell storage <folder> [--limit 1..500] [--html <new file>]");
            Console.WriteLine("killershell --version (also -v and /version)");
            Console.WriteLine("Output is JSON by default; an optional trailing --json is accepted.");
            Console.WriteLine("Export files (--csv, --html) must not exist yet; an existing file is refused with exit code 2.");
            Console.WriteLine("Exit codes: 0 succeeded, 1 the command failed, 2 usage error (bad or missing arguments, or help).");
        }

        private sealed class DisposableList<T> : IDisposable where T : IDisposable
        {
            internal IReadOnlyList<T> Items { get; }
            internal DisposableList(IEnumerable<T> items) => Items = [.. items];
            public void Dispose() { foreach (var item in Items) item.Dispose(); }
        }
    }
}
