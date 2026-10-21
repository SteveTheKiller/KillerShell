using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KillerShell.Models;

namespace KillerShell.Services
{
    // Multicore search: one producer walks the directory tree into a bounded queue,
    // one worker per core evaluates files, and the calling task pumps result batches
    // to the UI every ~150ms. Wildcard patterns are prepared ONCE per term/pattern
    // (the old code built a Regex per file per term). Cancellation stays graceful:
    // Stop drains quietly via ct.IsCancellationRequested - no exceptions escape.
    public class SearchEngine
    {
        private readonly Func<string, Stream> _openContent;
        private readonly Func<string, IEnumerable<string>> _enumerateFiles;
        private readonly Func<string, IEnumerable<string>> _enumerateDirectories;
        public int IncompletePaths { get; private set; }

        public SearchEngine() : this(path => new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, bufferSize: 65536)) { }

        internal SearchEngine(Func<string, Stream> openContent,
            Func<string, IEnumerable<string>>? enumerateFiles = null,
            Func<string, IEnumerable<string>>? enumerateDirectories = null)
        {
            _openContent = openContent ?? throw new ArgumentNullException(nameof(openContent));
            _enumerateFiles = enumerateFiles ?? Directory.EnumerateFiles;
            _enumerateDirectories = enumerateDirectories ?? Directory.EnumerateDirectories;
        }

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
                                           excludePatterns, caseSensitive, fileList, ct));
        }

        // ── Core search (producer / workers / UI pump) ───────────
        private void RunSearch(
            string             rootPath,
            IList<TermGroup>   groups,
            IList<SearchFilter> filters,
            string             includePatterns,
            string             excludePatterns,
            bool               caseSensitive,
            IList<string>?     fileList,
            CancellationToken  ct)
        {
            var comparison = caseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;

            // ---- One-time plan: patterns compiled up front, never per file ----
            var excludeNames = ParsePatterns(excludePatterns);
            var excludeRx    = excludeNames.Select(p => new WildcardPattern(p, false)).ToList();

            var includeNames = ParsePatterns(includePatterns);
            // Idiot-proofing: "*.*" / "*" mean "everything" - same as an empty include box.
            includeNames.RemoveAll(p => p == "*.*" || p == "*");
            var includeRx = includeNames.Select(p => new WildcardPattern(LoosenPattern(p), false)).ToList();

            var activeFilters = (filters ?? []).Where(f => f.IsActive).ToList();

            // Term plan: per group, the AND/OR mode plus each term's prepared name pattern
            // (null for content terms, which stream the file instead).
            var groupPlans = groups
                .Select(g => (And: g.Mode == TermGroup.GroupMode.And,
                              Terms: g.Terms
                                  .Where(t => !string.IsNullOrWhiteSpace(t.Pattern))
                                  .Select(t => (Term: t,
                                                NameRx: t.Mode == SearchTerm.SearchMode.FileName
                                                    ? new WildcardPattern(LoosenPattern(t.Pattern.Trim()), caseSensitive)
                                                    : null))
                                  .ToList()))
                .Where(p => p.Terms.Count > 0)
                .ToList();
            bool filterOnly = groupPlans.Count == 0 && activeFilters.Count > 0;

            // ---- Shared state (closures hoist these; counters via Interlocked) ----
            var    outQueue    = new ConcurrentQueue<SearchResult>();
            var incomplete = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            IncompletePaths = 0;
            int    processed   = 0;
            int    seq         = 0;
            string currentFile = string.Empty;   // reference writes are atomic; racy is fine for status

            // Per-file evaluation, run concurrently on the workers.
            void EvaluateFile(string filePath)
            {
                ct.ThrowIfCancellationRequested();
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
                        ct.ThrowIfCancellationRequested();
                        bool hit;
                        if (nameRx != null)
                        {
                            hit = nameRx.IsMatch(fileName);
                            if (hit) result.Matches.Add(new TermMatch { Term = term });
                        }
                        else
                        {
                            var lines = SearchContent(filePath, term.Pattern, comparison, ct,
                                () => incomplete.TryAdd(filePath, 0));
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
                    result.Created   = fi.CreationTime;
                }
                catch { /* unreadable - sorts to the bottom */ }
                result.Seq = Interlocked.Increment(ref seq) - 1;
                ct.ThrowIfCancellationRequested();
                outQueue.Enqueue(result);
            }

            // ---- Producer: walks the tree (or the piped list) into a bounded queue ----
            // The token is deliberately NOT handed to Task.Run or the waits below. Cancellation
            // here is cooperative: the loops watch ct themselves and drain to a clean finish, so
            // the producer's CompleteAdding always runs and no exception escapes RunSearch. A task
            // created with an already-canceled token never starts, which would leave the workers
            // blocked on the feed forever. CancellationToken.None says that on purpose.
            using var feed = new BlockingCollection<string>(boundedCapacity: 8192);
            var producer = Task.Run(() =>
            {
                try
                {
                    foreach (var f in fileList ?? SafeEnumerateFiles(rootPath, ct))
                    {
                        if (ct.IsCancellationRequested) break;
                        feed.Add(f, ct);   // throws OCE if canceled while the queue is full
                    }
                }
                catch (OperationCanceledException) { /* graceful stop */ }
                finally { feed.CompleteAdding(); }
            }, CancellationToken.None);

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
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* graceful stop */ }
                        catch { /* one bad file never kills a worker */ }
                    }
                }, CancellationToken.None);

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
                if (ct.IsCancellationRequested)
                {
                    while (outQueue.TryDequeue(out _)) { }
                    return false;
                }
                var batch = new List<SearchResult>(MaxBatch);
                while (batch.Count < MaxBatch && outQueue.TryDequeue(out var r)) batch.Add(r);
                if (batch.Count > 0 && !ct.IsCancellationRequested) ResultsBatch?.Invoke(batch);
                return !outQueue.IsEmpty;
            }

            void Flush()
            {
                while (FlushResults()) { }
                var cf = currentFile;
                if (cf.Length > 0) StatusChanged?.Invoke(cf);
                ProgressChanged?.Invoke(Volatile.Read(ref processed));
            }

            while (!Task.WaitAll(workers, UiIntervalMs, CancellationToken.None))
                Flush();
            try { producer.Wait(CancellationToken.None); } catch (AggregateException) { /* producer OCE already handled */ }
            Flush();   // final drain
            IncompletePaths = incomplete.Count;
        }

        // ── File enumeration ─────────────────────────────────────
        private IEnumerable<string> SafeEnumerateFiles(string root, CancellationToken ct)
        {
            var queue = new Queue<string>();
            queue.Enqueue(root);

            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                string dir = queue.Dequeue();
                IEnumerable<string> files = [];

                try { files = _enumerateFiles(dir); }
                catch { /* skip inaccessible */ }

                foreach (var f in EnumerateCancelable(files, ct))
                    yield return f;

                ct.ThrowIfCancellationRequested();
                IEnumerable<string> subdirs = [];
                try { subdirs = _enumerateDirectories(dir); }
                catch { /* skip inaccessible */ }

                foreach (var d in EnumerateCancelable(subdirs, ct))
                    queue.Enqueue(d);
            }
        }

        private static IEnumerable<string> EnumerateCancelable(IEnumerable<string> entries, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            using var iterator = entries.GetEnumerator();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                bool next = iterator.MoveNext();
                // A native enumeration call must return before managed cancellation can act.
                ct.ThrowIfCancellationRequested();
                if (!next) yield break;
                yield return iterator.Current;
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
        private List<LineMatch> SearchContent(
            string filePath, string pattern, StringComparison comparison, CancellationToken ct, Action incomplete)
        {
            var matches = new List<LineMatch>();
            try
            {
                ct.ThrowIfCancellationRequested();
                // Matching state is bounded by this limit, independent of logical line length.
                if (pattern.Length > 65536) throw new NotSupportedException("Content pattern exceeds the streaming limit.");
                using var fs = _openContent(filePath);

                // Check decoded characters so UTF-16/32 padding bytes are not mistaken
                // for binary content. Keep the probe bounded to the first 4 KB.
                var buf  = new byte[Math.Min(4096, fs.Length)];
                int read = fs.Read(buf, 0, buf.Length);
                ct.ThrowIfCancellationRequested();
                using (var sample = new StreamReader(new MemoryStream(buf, 0, read), Encoding.UTF8,
                                                     detectEncodingFromByteOrderMarks: true))
                    if (sample.ReadToEnd().IndexOf('\0') >= 0) return matches;
                fs.Seek(0, SeekOrigin.Begin);

                // Bound decoded reads so even a single long line can observe cancellation.
                using var reader = new StreamReader(fs, Encoding.UTF8,
                                                    detectEncodingFromByteOrderMarks: true,
                                                    bufferSize: 65536, leaveOpen: false);
                foreach (var line in ReadContentMatches(reader, pattern, comparison, ct))
                {
                    ct.ThrowIfCancellationRequested();
                    matches.Add(line);
                    // Retain the existing cap on displayed matching lines.
                    if (matches.Count >= 100) break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { incomplete(); }
            return matches;
        }

        private static IEnumerable<LineMatch> ReadContentMatches(StreamReader reader, string pattern,
            StringComparison comparison, CancellationToken ct)
        {
            var buffer = new char[4096];
            var chunk = new StringBuilder(4096);
            var preview = new StringBuilder(1024);
            string tail = string.Empty;
            bool matched = false, truncated = false, hasText = false;
            int lineNumber = 1;
            bool skipLf = false;

            void MatchChunk()
            {
                if (!matched && chunk.Length > 0)
                {
                    string window = tail + chunk.ToString();
                    matched = window.IndexOf(pattern, comparison) >= 0;
                    int keep = Math.Min(pattern.Length - 1, window.Length);
                    tail = matched || keep <= 0 ? string.Empty : window.Substring(window.Length - keep);
                }
                chunk.Clear();
            }
            LineMatch FinishLine()
            {
                string text = preview.ToString();
                if (truncated && text.Length > 0 && char.IsHighSurrogate(text[text.Length - 1]))
                    text = text.Substring(0, text.Length - 1);
                return new LineMatch { LineNumber = lineNumber, LineText = text.Trim() + (truncated ? "\u2026" : string.Empty) };
            }
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                // A native read already in progress must return before cancellation can stop it.
                int read = reader.Read(buffer, 0, buffer.Length);
                ct.ThrowIfCancellationRequested();
                if (read == 0) break;
                for (int i = 0; i < read; i++)
                {
                    char c = buffer[i];
                    if (skipLf)
                    {
                        skipLf = false;
                        if (c == '\n') continue;
                    }
                    if (c == '\r' || c == '\n')
                    {
                        MatchChunk();
                        if (matched) yield return FinishLine();
                        preview.Clear();
                        tail = string.Empty;
                        matched = truncated = hasText = false;
                        lineNumber++;
                        skipLf = c == '\r';
                    }
                    else
                    {
                        hasText = true;
                        if (preview.Length < 1024) preview.Append(c);
                        else truncated = true;
                        if (!matched)
                        {
                            chunk.Append(c);
                            if (chunk.Length == 4096) MatchChunk();
                        }
                    }
                }
            }
            MatchChunk();
            if (hasText && matched) yield return FinishLine();
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
        // or as a wildcard against the filename (prepared patterns).
        private static bool IsExcluded(string filePath, string fileName,
                                       List<string> excludeNames, List<WildcardPattern> excludeRx)
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

        /// <summary>One-off wildcard test (kept for callers outside the hot loop).</summary>
        public static bool MatchesWildcard(string input, string pattern, bool caseSensitive)
            => new WildcardPattern(pattern, caseSensitive).IsMatch(input);

        /// <summary>
        /// A whole-name pattern where * is any run of characters and ? is exactly one; every
        /// other character is literal.
        /// </summary>
        /// <remarks>
        /// Not a regex. Each * used to become .* in an unbounded regex, and a name that cannot
        /// match a pattern with many stars backtracked for longer than anyone would wait, inside
        /// a single IsMatch that cancellation cannot interrupt. This two-pointer matcher only ever
        /// returns to the most recent star, so a name is matched in at most (name length x
        /// pattern length) steps. Case-insensitive matching folds each character with the
        /// current culture, as RegexOptions.IgnoreCase did.
        /// </remarks>
        private sealed class WildcardPattern
        {
            private readonly string _pattern;
            private readonly bool _caseSensitive;
            private readonly System.Globalization.CultureInfo _culture;

            internal WildcardPattern(string pattern, bool caseSensitive)
            {
                _caseSensitive = caseSensitive;
                _culture = System.Globalization.CultureInfo.CurrentCulture;
                if (caseSensitive) { _pattern = pattern; return; }
                var folded = new char[pattern.Length];
                for (int i = 0; i < pattern.Length; i++) folded[i] = char.ToLower(pattern[i], _culture);
                _pattern = new string(folded);
            }

            internal bool IsMatch(string input)
            {
                int p = 0, i = 0, star = -1, resume = 0;
                while (i < input.Length)
                {
                    char c = _caseSensitive ? input[i] : char.ToLower(input[i], _culture);
                    if (p < _pattern.Length && _pattern[p] != '*' && (_pattern[p] == '?' || _pattern[p] == c))
                    {
                        p++;
                        i++;
                    }
                    else if (p < _pattern.Length && _pattern[p] == '*')
                    {
                        star = p++;
                        resume = i;
                    }
                    else if (star >= 0)
                    {
                        // Let the most recent star take one more character and try again.
                        p = star + 1;
                        i = ++resume;
                    }
                    else return false;
                }
                while (p < _pattern.Length && _pattern[p] == '*') p++;
                return p == _pattern.Length;
            }
        }
    }
}
