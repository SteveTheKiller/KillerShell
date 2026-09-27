using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using KillerShell.Models;
using KillerShell.Services;

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
            if (args.Length < 2)
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
            string path = Path.GetFullPath(args[1]);
            if (!File.Exists(path))
                throw new ArgumentException("Read path is not a file");

            int maximum = ReadSingleIntegerOption(args, "--max-chars", DefaultReadCharacters, 1, MaximumReadCharacters);
            var buffer = new char[maximum + 1];
            int count;
            using (var reader = new StreamReader(path, Encoding.UTF8, true))
                count = reader.ReadBlock(buffer, 0, buffer.Length);
            string text = new string(buffer, 0, Math.Min(count, maximum));
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

        private static int ReadSingleIntegerOption(string[] args, string option, int defaultValue, int minimum, int maximum)
        {
            if (args.Length == 2) return defaultValue;
            if (args.Length != 4 || args[2] != option
                || !int.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                || value < minimum || value > maximum)
                throw new ArgumentException(option + " must be between " + minimum.ToString(CultureInfo.InvariantCulture)
                    + " and " + maximum.ToString(CultureInfo.InvariantCulture));
            return value;
        }

        private static int Search(string[] args)
        {
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
            System.Threading.Tasks.Task.Run(() => engine.SearchAsync(root, new[] { group }, Array.Empty<SearchFilter>(),
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
        }
    }
}
