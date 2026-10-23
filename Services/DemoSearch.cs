using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KillerShell.Models;

namespace KillerShell.Services
{
    /// <summary>Searches the fabricated machine without opening or enumerating real files.</summary>
    internal static class DemoSearch
    {
        internal static Task<(List<SearchResult> Results, int Scanned)> SearchAsync(
            IEnumerable<string> rootPaths, IList<TermGroup> groups, IList<SearchFilter> filters,
            string includePatterns, string excludePatterns, bool caseSensitive, CancellationToken ct,
            Func<string, string> textFor, IList<string>? fileList = null)
            => Task.Run(() => Run(rootPaths, groups, filters, includePatterns, excludePatterns,
                caseSensitive, ct, textFor, fileList), ct);

        private static (List<SearchResult> Results, int Scanned) Run(
            IEnumerable<string> rootPaths, IList<TermGroup> groups, IList<SearchFilter> filters,
            string includePatterns, string excludePatterns, bool caseSensitive, CancellationToken ct,
            Func<string, string> textFor, IList<string>? fileList)
        {
            var results = new List<SearchResult>();
            int scanned = 0;
            var includes = Patterns(includePatterns).Where(p => p != "*" && p != "*.*")
                .Select(Loosen).ToList();
            var excludes = Patterns(excludePatterns);
            var activeFilters = filters.Where(f => f.IsActive).ToList();
            var plans = groups.Select(g => (And: g.Mode == TermGroup.GroupMode.And,
                Terms: g.Terms.Where(t => !string.IsNullOrWhiteSpace(t.Pattern)).ToList()))
                .Where(g => g.Terms.Count > 0).ToList();
            if (plans.Count == 0 && activeFilters.Count == 0) return (results, scanned);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            foreach (string path in fileList ?? Walk(rootPaths, ct))
            {
                ct.ThrowIfCancellationRequested();
                if (!seen.Add(path) || DemoFs.Find(path) is not { IsDir: false } entry) continue;
                scanned++;
                if (excludes.Any(p => path.IndexOf("\\" + p + "\\", StringComparison.OrdinalIgnoreCase) >= 0
                    || SearchEngine.MatchesWildcard(entry.Name, p, false))) continue;
                if (includes.Count > 0 && !includes.Any(p => SearchEngine.MatchesWildcard(entry.Name, p, false))) continue;
                if (!PassesFilters(entry, activeFilters)) continue;

                var result = new SearchResult
                {
                    FilePath = path, FileName = entry.Name, Directory = Path.GetDirectoryName(path) ?? string.Empty,
                    SizeBytes = entry.Size, Modified = entry.Modified, Created = entry.Created,
                };
                string? content = null;
                bool accepted = true;
                foreach (var plan in plans)
                {
                    bool groupMatches = plan.And;
                    foreach (var term in plan.Terms)
                    {
                        ct.ThrowIfCancellationRequested();
                        bool hit;
                        if (term.Mode == SearchTerm.SearchMode.FileName)
                        {
                            hit = SearchEngine.MatchesWildcard(entry.Name, Loosen(term.Pattern.Trim()), caseSensitive);
                            if (hit) result.Matches.Add(new TermMatch { Term = term });
                        }
                        else
                        {
                            content ??= textFor(path);
                            var lines = ContentMatches(content, term.Pattern, comparison, ct);
                            hit = lines.Count > 0;
                            if (hit) result.Matches.Add(new TermMatch { Term = term, Lines = lines });
                        }
                        groupMatches = plan.And ? groupMatches && hit : groupMatches || hit;
                    }
                    accepted &= groupMatches;
                }
                if (!accepted) continue;
                result.Seq = results.Count;
                results.Add(result);
            }
            ct.ThrowIfCancellationRequested();
            return (results, scanned);
        }

        private static IEnumerable<string> Walk(IEnumerable<string> roots, CancellationToken ct)
        {
            var folders = new Queue<string>(SearchRoots.Normalize(roots));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (folders.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                string folder = folders.Dequeue();
                if (!seen.Add(folder)) continue;
                foreach (var entry in DemoFs.Children(folder))
                {
                    ct.ThrowIfCancellationRequested();
                    string path = Path.Combine(folder, entry.Name);
                    if (entry.IsDir) folders.Enqueue(path);
                    else yield return path;
                }
            }
        }

        private static bool PassesFilters(DemoFs.Entry entry, List<SearchFilter> filters)
        {
            foreach (var filter in filters)
            {
                if (filter.IsExt)
                {
                    string extension = Path.GetExtension(entry.Name).TrimStart('.');
                    bool hit = filter.Text.Split(';').Select(p => p.Trim().TrimStart('*').TrimStart('.').Trim())
                        .Any(p => p.Length > 0 && string.Equals(extension, p, StringComparison.OrdinalIgnoreCase));
                    if (filter.ConditionIndex == 0 ? !hit : hit) return false;
                }
                else if (filter.IsDate)
                {
                    var day = filter.Date!.Value.Date;
                    bool hit = filter.ConditionIndex == 0 ? entry.Modified < day : entry.Modified.Date > day;
                    if (!hit) return false;
                }
                else if (filter.IsSize)
                {
                    bool hit = filter.ConditionIndex == 0 ? entry.Size > filter.SizeBytes : entry.Size < filter.SizeBytes;
                    if (!hit) return false;
                }
            }
            return true;
        }

        private static List<LineMatch> ContentMatches(string content, string pattern,
            StringComparison comparison, CancellationToken ct)
        {
            var matches = new List<LineMatch>();
            using var reader = new StringReader(content);
            int number = 0;
            string? line;
            while (matches.Count < 100 && (line = reader.ReadLine()) != null)
            {
                ct.ThrowIfCancellationRequested();
                number++;
                if (line.IndexOf(pattern, comparison) >= 0)
                    matches.Add(new LineMatch { LineNumber = number, LineText = line.Trim() });
            }
            return matches;
        }

        private static List<string> Patterns(string raw) => raw.Split(';')
            .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

        private static string Loosen(string pattern)
        {
            if (pattern.IndexOf('*') >= 0 || pattern.IndexOf('?') >= 0) return pattern;
            return pattern.StartsWith(".", StringComparison.Ordinal) ? "*" + pattern : "*" + pattern + "*";
        }
    }
}
