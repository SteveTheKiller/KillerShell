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
        /// <summary>
        /// Every path is probed at once on the thread pool, and the caller waits at most
        /// <paramref name="budget"/> in all. A probe still running then (a share that has not
        /// answered) counts as unreachable for this call and is left to finish on its own.
        /// Order is kept.
        /// </summary>
        internal static List<string> Reachable(IEnumerable<string> paths, Func<string, bool> probe, TimeSpan budget)
        {
            var list = paths.ToList();
            // A thread each rather than the pool: a busy pool (a scan, a search) would queue the
            // probes behind its own work and spend the whole budget before one even started.
            var probes = list.Select(path => Task.Factory.StartNew(() => { try { return probe(path); } catch { return false; } },
                System.Threading.CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            try { Task.WaitAll(probes, budget); } catch { }
            var live = new List<string>();
            for (int i = 0; i < list.Count; i++)
                if (probes[i].Status == TaskStatus.RanToCompletion && probes[i].Result) live.Add(list[i]);
            return live;
        }
    }
}
