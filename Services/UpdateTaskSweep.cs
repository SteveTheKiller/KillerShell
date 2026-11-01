using System;
using System.Collections.Generic;

namespace KillerShell.Services
{
    /// <summary>
    /// Deletes the hidden one-time "KillerUpdate-*" tasks an elevated update helper registers to
    /// reopen the app. The helper removes its own task, but one killed before its cleanup stays in
    /// Task Scheduler forever. Tasks still queued or running belong to a start in progress and stay.
    /// </summary>
    internal static class UpdateTaskSweep
    {
        private const string Prefix = "KillerUpdate-";
        private const int Queued = 2, Running = 4;

        /// <summary>The scheduler as the sweep sees it: every task in the root folder with its state, and a delete.</summary>
        internal sealed class Scheduler
        {
            internal Func<IEnumerable<(string Name, int State)>> Tasks = RootTasks;
            internal Action<string> Delete = DeleteRootTask;
        }

        /// <summary>Removes the finished update tasks. Never throws: Task Scheduler can be unavailable,
        /// and a task this account may not delete is left for an account that may.</summary>
        internal static int Run(Scheduler? scheduler = null)
        {
            scheduler ??= new Scheduler();
            int removed = 0;
            try
            {
                foreach (var (name, state) in new List<(string Name, int State)>(scheduler.Tasks()))
                {
                    if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || state == Queued || state == Running) continue;
                    try { scheduler.Delete(name); removed++; } catch { }
                }
            }
            catch { }
            return removed;
        }

        private static IEnumerable<(string Name, int State)> RootTasks()
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
            service.Connect();
            dynamic folder = service.GetFolder("\\");
            var found = new List<(string, int)>();
            foreach (dynamic task in folder.GetTasks(1)) found.Add(((string)task.Name, (int)task.State));
            return found;
        }

        private static void DeleteRootTask(string name)
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
            service.Connect();
            service.GetFolder("\\").DeleteTask(name, 0);
        }
    }
}
