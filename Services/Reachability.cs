using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace KillerShell.Services
{
    /// <summary>
    /// Which of a list of folders can be reached right now, without letting one dead network
    /// path stall the caller.
    /// </summary>
    internal static class Reachability
    {
        // Checks still running, one per path. A dead share can take the whole SMB timeout to
        // answer; a second check of it while the first is still stuck joins the first instead of
        // parking another thread on the same share. Because a joined check is shared, every
        // caller's probe must give the same answer for a path (all of them are Directory.Exists).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<bool>> Running =
            new(StringComparer.OrdinalIgnoreCase);

        // A thread each rather than the pool: a busy pool (a scan, a search) would queue the
        // probes behind its own work and spend the whole budget before one even started.
        private static Task<bool> Start(string path, Func<string, bool> probe)
        {
            var task = Task.Factory.StartNew(() => { try { return probe(path); } catch { return false; } },
                System.Threading.CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            task.ContinueWith(done => Forget(path, done), TaskScheduler.Default);
            return task;
        }

        // The running check for a path, or a new one. A finished check found here is an answer
        // from before (its cleanup ran before it was stored) and is replaced, never reused.
        private static Task<bool> Join(string path, Func<string, bool> probe)
        {
            var task = Running.GetOrAdd(path, key => Start(key, probe));
            if (!task.IsCompleted) return task;
            Forget(path, task);
            return Running.GetOrAdd(path, key => Start(key, probe));
        }

        private static void Forget(string path, Task<bool> task)
            => ((ICollection<KeyValuePair<string, Task<bool>>>)Running).Remove(new KeyValuePair<string, Task<bool>>(path, task));

        /// <summary>
        /// Every path is probed at once on the thread pool, and the caller waits at most
        /// <paramref name="budget"/> in all. A probe still running then (a share that has not
        /// answered) counts as unreachable for this call and is left to finish on its own.
        /// Order is kept.
        /// </summary>
        internal static List<string> Reachable(IEnumerable<string> paths, Func<string, bool> probe, TimeSpan budget)
        {
            var list = paths.ToList();
            var probes = list.Select(path => Join(path, probe)).ToArray();
            try { Task.WaitAll(probes, budget); } catch { }
            var live = new List<string>();
            for (int i = 0; i < list.Count; i++)
                if (probes[i].Status == TaskStatus.RanToCompletion && probes[i].Result) live.Add(list[i]);
            return live;
        }
    }
}
