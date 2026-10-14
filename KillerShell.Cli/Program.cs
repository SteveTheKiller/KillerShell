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

        internal static int Main(string[] args)
        {
            try { Console.OutputEncoding = new UTF8Encoding(false); }
            catch (IOException) { /* A GUI process with redirected output can lack a console handle. */ }
            if (args.Length == 1 && args[0] == "--version")
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
            string text = new(buffer, 0, Math.Min(count, maximum));
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
            int limit = ReadSingleIntegerOption(args, "--limit", DefaultLimit, 1, MaximumLimit, 1);
            var rows = Process.GetProcesses().OrderBy(item => item.ProcessName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Id).Take(limit + 1).ToList();
            bool limitReached = rows.Count > limit;
            if (limitReached) rows.RemoveAt(rows.Count - 1);
            var output = new StringBuilder("{\"processes\":[");
            for (int i = 0; i < rows.Count; i++)
            {
                using var process = rows[i];
                if (i > 0) output.Append(',');
                output.Append("{\"name\":").Append(JsonString(process.ProcessName));
                output.Append(",\"pid\":").Append(process.Id.ToString(CultureInfo.InvariantCulture));
                try { output.Append(",\"memoryBytes\":").Append(process.WorkingSet64.ToString(CultureInfo.InvariantCulture)); }
                catch { output.Append(",\"memoryBytes\":null"); }
                output.Append('}');
            }
            output.Append("],\"limitReached\":").Append(limitReached ? "true" : "false").Append('}');
            Console.WriteLine(output.ToString());
            return 0;
        }

        private static int Services(string[] args)
        {
            int limit = ReadSingleIntegerOption(args, "--limit", DefaultLimit, 1, MaximumLimit, 1);
            using var services = new DisposableList<ServiceController>(ServiceController.GetServices());
            var rows = services.Items.OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase).Take(limit + 1).ToList();
            bool limitReached = rows.Count > limit;
            if (limitReached) rows.RemoveAt(rows.Count - 1);
            var output = new StringBuilder("{\"services\":[");
            for (int i = 0; i < rows.Count; i++)
            {
                if (i > 0) output.Append(',');
                var service = rows[i];
                output.Append("{\"name\":").Append(JsonString(service.ServiceName));
                output.Append(",\"displayName\":").Append(JsonString(service.DisplayName));
                output.Append(",\"status\":").Append(JsonString(service.Status.ToString())).Append('}');
            }
            output.Append("],\"limitReached\":").Append(limitReached ? "true" : "false").Append('}');
            Console.WriteLine(output.ToString());
            return 0;
        }

        private static int Events(string[] args)
        {
            if (args.Length < 2 || args.Length > 4) throw new ArgumentException("Events need a log name and optional limit");
            string log = args[1];
            if (log != "Application" && log != "System" && log != "Security")
                throw new ArgumentException("Log must be Application, System, or Security");
            int limit = ReadSingleIntegerOption(args, "--limit", 50, 1, 100, 2);
            var query = new EventLogQuery(log, PathType.LogName) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            var output = new StringBuilder("{\"log\":").Append(JsonString(log)).Append(",\"events\":[");
            int count = 0;
            for (; count < limit; count++)
            {
                using EventRecord record = reader.ReadEvent();
                if (record == null) break;
                if (count > 0) output.Append(',');
                output.Append("{\"id\":").Append(record.Id.ToString(CultureInfo.InvariantCulture));
                output.Append(",\"level\":").Append(JsonString(record.LevelDisplayName ?? string.Empty));
                output.Append(",\"provider\":").Append(JsonString(record.ProviderName ?? string.Empty));
                output.Append(",\"timeUtc\":").Append(record.TimeCreated.HasValue ? JsonString(record.TimeCreated.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)) : "null");
                string message;
                try { message = record.FormatDescription() ?? string.Empty; } catch { message = string.Empty; }
                output.Append(",\"message\":").Append(JsonString(message.Length <= 2000 ? message : message[..2000])).Append('}');
            }
            output.Append("],\"limitReached\":").Append(count == limit ? "true" : "false").Append('}');
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
                "HKEY_CLASSES_ROOT" => Microsoft.Win32.Registry.ClassesRoot,
                "HKEY_CURRENT_USER" => Microsoft.Win32.Registry.CurrentUser,
                "HKEY_LOCAL_MACHINE" => Microsoft.Win32.Registry.LocalMachine,
                "HKEY_USERS" => Microsoft.Win32.Registry.Users,
                "HKEY_CURRENT_CONFIG" => Microsoft.Win32.Registry.CurrentConfig,
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
                    default: throw new ArgumentException("Unknown search option: " + args[i]);
                }
            }
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(content))
                throw new ArgumentException("Provide --name or --content");

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

            var output = new StringBuilder("{\"results\":[");
            bool first = true;
            foreach (var item in found.OrderBy(item => item.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                if (!first) output.Append(',');
                first = false;
                output.Append("{\"path\":").Append(JsonString(item.FilePath));
                output.Append(",\"sizeBytes\":").Append(item.SizeBytes.ToString(CultureInfo.InvariantCulture));
                output.Append(",\"modifiedUtc\":").Append(JsonString(item.Modified.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)));
                output.Append('}');
            }
            output.Append("],\"limitReached\":").Append(cancellation.IsCancellationRequested ? "true" : "false").Append('}');
            Console.WriteLine(output.ToString());
            return 0;
        }

        private static string JsonString(string value)
        {
            var output = new StringBuilder("\"");
            foreach (char character in value)
            {
                if (character == '"' || character == '\\') output.Append('\\').Append(character);
                else if (character < 0x20) output.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                else output.Append(character);
            }
            return output.Append('"').ToString();
        }

        private static void PrintUsage()
        {
            Console.WriteLine("KillerShell.Cli search <folder> --name <pattern> [--content <text>] [--limit 1..500]");
            Console.WriteLine("KillerShell.Cli search <folder> --content <text> [--limit 1..500]");
            Console.WriteLine("KillerShell.Cli list <folder> [--limit 1..500]");
            Console.WriteLine("KillerShell.Cli info <path>");
            Console.WriteLine("KillerShell.Cli read <file> [--max-chars 1..65536]");
            Console.WriteLine("KillerShell.Cli processes [--limit 1..500]");
            Console.WriteLine("KillerShell.Cli services [--limit 1..500]");
            Console.WriteLine("KillerShell.Cli events <Application|System|Security> [--limit 1..100]");
            Console.WriteLine("KillerShell.Cli registry <hive\\key> [--limit 1..100]");
            Console.WriteLine("KillerShell.Cli drives");
            Console.WriteLine("KillerShell.Cli hash <file>");
        }

        private sealed class DisposableList<T> : IDisposable where T : IDisposable
        {
            internal IReadOnlyList<T> Items { get; }
            internal DisposableList(IEnumerable<T> items) => Items = [.. items];
            public void Dispose() { foreach (var item in Items) item.Dispose(); }
        }
    }
}
