using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KillerShell.Models;

namespace KillerShell.Services
{
    // Multicore search: one producer walks the directory tree into a bounded queue,
    // one worker per core evaluates files, and the calling task pumps result batches
    // to the UI every ~150ms. Wildcard regexes are compiled ONCE per term/pattern
    // (the old code built a Regex per file per term). Cancellation stays graceful:
    // Stop drains quietly via ct.IsCancellationRequested - no exceptions escape.
    public class SearchEngine
    {
        // ── Events (invoked from the pump task; the UI marshals via Dispatcher) ──
        public event Action<List<SearchResult>>? ResultsBatch;   // flushed every ~150 ms
        public event Action<string>?             StatusChanged;
        public event Action<int>?                ProgressChanged; // files scanned so far

        // ── Public entry point ───────────────────────────────────
        // fileList: when non-null the engine searches THAT list of files (a piped
        // snapshot of another search's results) instead of walking rootPath.
        public async Task SearchAsync(
            string             rootPath,
            IList<TermGroup>   groups,
            IList<SearchFilter> filters,          // dropdown filter rows, AND-ed with terms
            string             includePatterns,   // e.g. "*.txt;*.log"
            string             excludePatterns,   // e.g. "bin;obj;*.min.js"
            bool               caseSensitive,
            CancellationToken  ct,
            IList<string>?     fileList = null)
        {
            await Task.Run(() => RunSearch(rootPath, groups, filters, includePatterns,
                                           excludePatterns, caseSensitive, ct, fileList));
        }

        // ── Core search (producer / workers / UI pump) ───────────
        private void RunSearch(
            string             rootPath,
            IList<TermGroup>   groups,
            IList<SearchFilter> filters,
            string             includePatterns,
            string             excludePatterns,
            bool               caseSensitive,
            CancellationToken  ct,
            IList<string>?     fileList)
        {
            var comparison = caseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            // ---- One-time plan: patterns compiled up front, never per file ----
            var excludeNames = ParsePatterns(excludePatterns);
            var excludeRx    = excludeNames.Select(p => WildcardRegex(p, false)).ToList();

            var includeNames = ParsePatterns(includePatterns);
            // Idiot-proofing: "*.*" / "*" mean "everything" - same as an empty include box.
            includeNames.RemoveAll(p => p == "*.*" || p == "*");
            var includeRx = includeNames.Select(p => WildcardRegex(LoosenPattern(p), false)).ToList();

            var activeFilters = (filters ?? []).Where(f => f.IsActive).ToList();

            // Term plan: per group, the AND/OR mode plus each term's compiled name regex
            // (null for content terms, which stream the file instead).
            var groupPlans = groups
                .Select(g => (And: g.Mode == TermGroup.GroupMode.And,
                              Terms: g.Terms
                                  .Where(t => !string.IsNullOrWhiteSpace(t.Pattern))
                                  .Select(t => (Term: t,
                                                NameRx: t.Mode == SearchTerm.SearchMode.FileName
                                                    ? WildcardRegex(LoosenPattern(t.Pattern.Trim()), caseSensitive)
                                                    : null))
                                  .ToList()))
                .Where(p => p.Terms.Count > 0)
                .ToList();
            bool filterOnly = groupPlans.Count == 0 && activeFilters.Count > 0;

            // ---- Shared state (closures hoist these; counters via Interlocked) ----
            var    outQueue    = new ConcurrentQueue<SearchResult>();
            int    processed   = 0;
            int    seq         = 0;
            string currentFile = string.Empty;   // reference writes are atomic; racy is fine for status

            // Per-file evaluation, run concurrently on the workers.
            void EvaluateFile(string filePath)
            {
                // Piped lists are snapshots - files may have vanished since pass 1.
                if (fileList != null && !File.Exists(filePath))
                { Interlocked.Increment(ref processed); return; }

                string fileName = Path.GetFileName(filePath);

                if (IsExcluded(filePath, fileName, excludeNames, excludeRx))
                { Interlocked.Increment(ref processed); return; }

                if (includeRx.Count > 0 && !includeRx.Any(rx => rx.IsMatch(fileName)))
                { Interlocked.Increment(ref processed); return; }

                if (activeFilters.Count > 0 && !PassesFilters(filePath, fileName, activeFilters))
                { Interlocked.Increment(ref processed); return; }

                Interlocked.Increment(ref processed);
                currentFile = filePath;

                var result = new SearchResult
                {
                    FilePath  = filePath,
                    FileName  = fileName,
                    Directory = Path.GetDirectoryName(filePath) ?? string.Empty
                };

                bool fileMatches = true;   // groups are AND-ed together
                foreach (var (And, Terms) in groupPlans)
                {
                    bool groupSat = And;   // AND starts true, OR starts false
                    foreach (var (term, nameRx) in Terms)
                    {
                        bool hit;
                        if (nameRx != null)
                        {
                            hit = nameRx.IsMatch(fileName);
                            if (hit) result.Matches.Add(new TermMatch { Term = term });
                        }
                        else
                        {
                            var lines = SearchContent(filePath, term.Pattern, comparison);
                            hit = lines.Count > 0;
                            if (hit) result.Matches.Add(new TermMatch { Term = term, Lines = lines });
                        }
                        groupSat = And ? (groupSat && hit) : (groupSat || hit);
                    }
                    fileMatches = fileMatches && groupSat;
                }

                bool ok = groupPlans.Count > 0 ? fileMatches : filterOnly;
                if (!ok) return;

                // One stat per RESULT (cheap) so the UI can sort by size/date.
                try
                {
                    var fi = new FileInfo(filePath);
                    result.SizeBytes = fi.Length;
                    result.Modified  = fi.LastWriteTime;
                }
                catch { /* unreadable - sorts to the bottom */ }
                result.Seq = Interlocked.Increment(ref seq) - 1;
                outQueue.Enqueue(result);
            }

            // ---- Producer: walks the tree (or the piped list) into a bounded queue ----
            using var feed = new BlockingCollection<string>(boundedCapacity: 8192);
            var producer = Task.Run(() =>
            {
                try
                {
                    foreach (var f in fileList ?? SafeEnumerateFiles(rootPath))
                    {
                        if (ct.IsCancellationRequested) break;
                        feed.Add(f, ct);   // throws OCE if canceled while the queue is full
                    }
                }
                catch (OperationCanceledException) { /* graceful stop */ }
                finally { feed.CompleteAdding(); }
            });

            // ---- Workers: one per core, capped so a Threadripper doesn't thrash I/O ----
            int workerCount = Math.Max(2, Math.Min(16, Environment.ProcessorCount));
            var workers = new Task[workerCount];
            for (int i = 0; i < workerCount; i++)
                workers[i] = Task.Run(() =>
                {
                    foreach (var filePath in feed.GetConsumingEnumerable())
                    {
                        if (ct.IsCancellationRequested) continue;   // drain fast, evaluate nothing
                        try { EvaluateFile(filePath); }
                        catch { /* one bad file never kills a worker */ }
                    }
                });

            // ---- UI pump: this task flushes batches every ~150ms until the workers finish ----
            const int UiIntervalMs = 150;

            // Each ResultsBatch becomes exactly ONE dispatcher callback on the UI thread, and a
            // callback cannot be interrupted once it has started - DispatcherPriority only orders
            // work that is still queued. So draining the whole queue into a single batch froze the
            // window for as long as it took to add every result, and no priority could help: a
            // broad match like "log" can put tens of thousands of hits in one 150ms window.
            // Slices are capped instead. A backlog is drained by posting slices back to back
            // rather than waiting out another tick, so throughput is unchanged - only the size of
            // any one UI callback is bounded, which is what lets input interleave.
            const int MaxBatch = 250;

            // Posts at most MaxBatch results. Returns true while the queue still holds more.
            bool FlushResults()
            {
                var batch = new List<SearchResult>(MaxBatch);
                while (batch.Count < MaxBatch && outQueue.TryDequeue(out var r)) batch.Add(r);
                if (batch.Count > 0) ResultsBatch?.Invoke(batch);
                return !outQueue.IsEmpty;
            }

            void Flush()
            {
                while (FlushResults()) { }
                var cf = currentFile;
                if (cf.Length > 0) StatusChanged?.Invoke(cf);
                ProgressChanged?.Invoke(Volatile.Read(ref processed));
            }

            while (!Task.WaitAll(workers, UiIntervalMs))
                Flush();
            try { producer.Wait(); } catch (AggregateException) { /* producer OCE already handled */ }
            Flush();   // final drain
        }

        // ── File enumeration ─────────────────────────────────────
        private static IEnumerable<string> SafeEnumerateFiles(string root)
        {
            var queue = new Queue<string>();
            queue.Enqueue(root);

            while (queue.Count > 0)
            {
                string dir = queue.Dequeue();
                IEnumerable<string> files = [];

                try { files = Directory.EnumerateFiles(dir); }
                catch { /* skip inaccessible */ }

                foreach (var f in files)
                    yield return f;

                IEnumerable<string> subdirs = [];
                try { subdirs = Directory.EnumerateDirectories(dir); }
                catch { /* skip inaccessible */ }

                foreach (var d in subdirs)
                    queue.Enqueue(d);
            }
        }

        // ── Filter evaluation ────────────────────────────────────
        // Extension/date/size checks - no file content is read here. Date semantics:
        // "before" = strictly before that day's midnight; "after" = strictly
        // after that day ends (the chosen day itself is excluded by both).
        private static bool PassesFilters(string filePath, string fileName, List<SearchFilter> filters)
        {
            foreach (var f in filters)
            {
                switch (f.FieldIndex)
                {
                    case SearchFilter.FieldExt:
                    {
                        bool match = ExtensionMatches(fileName, f.Text);
                        if (f.ConditionIndex == 0 ? !match : match) return false;
                        break;
                    }
                    case SearchFilter.FieldDate:
                    {
                        DateTime mod;
                        try { mod = File.GetLastWriteTime(filePath); }
                        catch { return false; }
                        var day = f.Date!.Value.Date;
                        bool ok = f.ConditionIndex == 0 ? mod < day : mod >= day.AddDays(1);
                        if (!ok) return false;
                        break;
                    }
                    case SearchFilter.FieldSize:
                    {
                        long len;
                        try { len = new FileInfo(filePath).Length; }
                        catch { return false; }
                        bool ok = f.ConditionIndex == 0 ? len > f.SizeBytes : len < f.SizeBytes;
                        if (!ok) return false;
                        break;
                    }
                }
            }
            return true;
        }

        // Extension filter accepts anything an idiot might type: "log", ".log",
        // "*.log", or a ; list like "log; tmp". Always case-insensitive.
        private static bool ExtensionMatches(string fileName, string raw)
        {
            string ext = Path.GetExtension(fileName).TrimStart('.');
            foreach (var part in raw.Split(';'))
            {
                var t = part.Trim().TrimStart('*').TrimStart('.').Trim();
                if (t.Length == 0) continue;
                if (string.Equals(ext, t, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        // ── Content search ───────────────────────────────────────
        private static List<LineMatch> SearchContent(
            string filePath, string pattern, StringComparison comparison)
        {
            var matches = new List<LineMatch>();
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                                              FileShare.Read, bufferSize: 65536);

                // Skip likely binary files - null byte in first 4 KB
                var buf  = new byte[Math.Min(4096, fs.Length)];
                int read = fs.Read(buf, 0, buf.Length);
                for (int i = 0; i < read; i++)
                    if (buf[i] == 0) return matches;
                fs.Seek(0, SeekOrigin.Begin);

                // Stream line-by-line - never loads the whole file into memory
                using var reader = new StreamReader(fs, Encoding.UTF8,
                                                    detectEncodingFromByteOrderMarks: true,
                                                    bufferSize: 65536, leaveOpen: false);
                string? line;
                int lineNum = 0;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNum++;
                    if (line.IndexOf(pattern, comparison) >= 0)
                    {
                        matches.Add(new LineMatch { LineNumber = lineNum, LineText = line.Trim() });
                        // Enough to prove the hit and fill the UI - a 10k-hit log file
                        // would otherwise build a skyscraper of a result card.
                        if (matches.Count >= 100) break;
                    }
                }
            }
            catch { /* unreadable file - skip */ }
            return matches;
        }

        // ── Pattern helpers ──────────────────────────────────────
        private static List<string> ParsePatterns(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return [];
            return [.. raw.Split(';')
                      .Select(p => p.Trim())
                      .Where(p => p.Length > 0)];
        }

        // Excludes match two ways: as a whole path segment ("bin", "node_modules")
        // or as a wildcard against the filename (precompiled regexes).
        private static bool IsExcluded(string filePath, string fileName,
                                       List<string> excludeNames, List<Regex> excludeRx)
        {
            for (int i = 0; i < excludeNames.Count; i++)
            {
                if (filePath.IndexOf(Path.DirectorySeparatorChar + excludeNames[i] + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                if (excludeRx[i].IsMatch(fileName))
                    return true;
            }
            return false;
        }

        // Idiot-proofing for name patterns: no wildcards becomes a contains-match
        // ("log" -> "*log*"), a bare extension gets its star (".log" -> "*.log").
        // Patterns that already use * or ? pass through as-is.
        private static string LoosenPattern(string p)
        {
            if (p.IndexOf('*') >= 0 || p.IndexOf('?') >= 0) return p;
            if (p.StartsWith(".")) return "*" + p;
            return "*" + p + "*";
        }

        // Compiled once per pattern; the workers only call IsMatch.
        private static Regex WildcardRegex(string pattern, bool caseSensitive)
        {
            string rx = "^" + Regex.Escape(pattern)
                            .Replace("\\*", ".*")
                            .Replace("\\?", ".") + "$";
            var opts = RegexOptions.Compiled | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
            return new Regex(rx, opts);
        }

        /// <summary>One-off wildcard test (kept for callers outside the hot loop).</summary>
        public static bool MatchesWildcard(string input, string pattern, bool caseSensitive)
        {
            string regex = "^" + Regex.Escape(pattern)
                               .Replace("\\*", ".*")
                               .Replace("\\?", ".") + "$";
            var opts = caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;
            return Regex.IsMatch(input, regex, opts);
        }
    }
}
