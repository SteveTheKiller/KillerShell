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
            if (args.Length < 2 || args[0] != "search")
            {
                PrintUsage();
                return 2;
            }

            try
            {
                return Search(args);
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
        }
    }
}
