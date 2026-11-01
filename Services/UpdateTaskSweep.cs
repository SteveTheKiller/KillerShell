using System;
using System.Collections.Generic;

namespace KillerShell.Services
{
    /// <summary>
    /// Deletes the hidden one-time "KillerUpdate-*" tasks an elevated update helper registers to
    /// reopen the app. The helper removes its own task, but one killed before its cleanup stays in
    /// Task Scheduler forever. Tasks still queued or running belong to a start in progress and stay, and so does one
    /// registered only minutes ago, which its helper has not started yet.
    /// </summary>
    internal static class UpdateTaskSweep
    {
        private const string Prefix = "KillerUpdate-";
        private const int Queued = 2, Running = 4;
        private static readonly TimeSpan MinimumAge = TimeSpan.FromMinutes(5);

        /// <summary>The scheduler as the sweep sees it: every task in the root folder with its state and registration time (UTC), a delete and the clock.</summary>
        internal sealed class Scheduler
        {
            internal Func<IEnumerable<(string Name, int State, DateTime Registered)>> Tasks = RootTasks;
            internal Action<string> Delete = DeleteRootTask;
            internal Func<DateTime> Now = () => DateTime.UtcNow;
        }

        /// <summary>Removes the finished update tasks. Never throws: Task Scheduler can be unavailable,
        /// and a task this account may not delete is left for an account that may.</summary>
        internal static int Run(Scheduler? scheduler = null)
        {
            scheduler ??= new Scheduler();
            int removed = 0;
            try
            {
                foreach (var (name, state, registered) in new List<(string Name, int State, DateTime Registered)>(scheduler.Tasks()))
                {
                    if (!name.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || state == Queued || state == Running) continue;
                    if (scheduler.Now() - registered < MinimumAge) continue;
                    try { scheduler.Delete(name); removed++; } catch { }
                }
            }
            catch { }
            return removed;
        }

        private static IEnumerable<(string Name, int State, DateTime Registered)> RootTasks()
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
            service.Connect();
            dynamic folder = service.GetFolder("\\");
            var found = new List<(string, int, DateTime)>();
            foreach (dynamic task in folder.GetTasks(1)) found.Add(((string)task.Name, (int)task.State, Registered(task)));
            return found;
        }

        // An unreadable registration date counts as old, so one odd task cannot stay forever.
        private static DateTime Registered(dynamic task)
        {
            try
            {
                if (DateTime.TryParse((string)task.Definition.RegistrationInfo.Date, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeLocal, out var date)) return date;
            }
            catch { }
            return DateTime.MinValue;
        }

        private static void DeleteRootTask(string name)
        {
            dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
            service.Connect();
            service.GetFolder("\\").DeleteTask(name, 0);
        }
    }
}
