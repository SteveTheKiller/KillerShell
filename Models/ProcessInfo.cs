using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KillerShell.Models
{
    /// <summary>
    /// One row of the Task Manager tab (Shell/ProcessListControl.cs): a live process, resampled
    /// on every refresh tick rather than rebuilt, so a row's identity survives a tick and the
    /// grid's selection/scroll position does too.
    /// </summary>
    /// <remarks>
    /// Notifying, not a plain record - the same reason KillerNotes' sidebar rows have to notify
    /// (see the note in CLAUDE.md): this object is bound in a live DataGrid row and edited IN
    /// PLACE on every tick rather than replaced, so CpuPercent/MemoryBytes have to raise
    /// PropertyChanged or the cells they are bound to would freeze at whatever they first showed.
    /// </remarks>
    public class ProcessInfo : INotifyPropertyChanged
    {
        public int Pid { get; }

        public ProcessInfo(int pid) => Pid = pid;

        private string _name = string.Empty;
        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; Notify(); } }
        }

        // "-" when the owner could not be determined (ProcessListControl caches this once per
        // PID rather than asking WMI's GetOwner() every tick - see the cache comment there).
        private string _user = "-";
        public string User
        {
            get => _user;
            set { if (_user != value) { _user = value; Notify(); } }
        }

        private double _cpuPercent;
        public double CpuPercent
        {
            get => _cpuPercent;
            set { if (_cpuPercent != value) { _cpuPercent = value; Notify(); Notify(nameof(CpuLabel)); } }
        }

        private long _memoryBytes;
        public long MemoryBytes
        {
            get => _memoryBytes;
            set { if (_memoryBytes != value) { _memoryBytes = value; Notify(); Notify(nameof(MemoryLabel)); } }
        }

        // Empty when WMI could not answer (a protected/elevated process and we are not
        // elevated) - shown as an empty cell rather than an error, same as Path below.
        private string _commandLine = string.Empty;
        public string CommandLine
        {
            get => _commandLine;
            set { if (_commandLine != value) { _commandLine = value; Notify(); } }
        }

        // The exe's full path, or empty when it could not be read (WMI's ExecutablePath comes
        // back null rather than throwing for a process we cannot see into, unlike
        // Process.MainModule.FileName - see ProcessListControl.RefreshFromWmi). Empty disables
        // "Open file location" and "Restart" in the row's context menu.
        private string _path = string.Empty;
        public string Path
        {
            get => _path;
            set { if (_path != value) { _path = value; Notify(); Notify(nameof(HasPath)); } }
        }

        public bool HasPath => Path.Length > 0;

        // Read from the SAME bulk Win32_Process query CommandLine/Path already come from
        // (ProcessListControl.QueryWmiProcesses just carries one more column now) - never a
        // second per-row query, the same discipline every other WMI-sourced field here follows.
        // "-" when WMI has no answer, same convention as User.
        private string _parentPid = "-";
        public string ParentPid
        {
            get => _parentPid;
            set { if (_parentPid != value) { _parentPid = value; Notify(); } }
        }

        // Process.StartTime throws for a protected/elevated process this app is not running as -
        // read defensively in ProcessListControl.BuildSamples, per-process, the same way CPU% is;
        // no WMI round trip needed since Process already exposes it directly.
        private string _startTimeLabel = "-";
        public string StartTimeLabel
        {
            get => _startTimeLabel;
            set { if (_startTimeLabel != value) { _startTimeLabel = value; Notify(); } }
        }

        public string CpuLabel => CpuPercent.ToString("0.0") + " %";

        // Same ladder as SearchResult.SizeLabel (Models/SearchResult.cs), so a memory figure and
        // a file size read the same way anywhere in the app.
        public string MemoryLabel
        {
            get
            {
                long b = MemoryBytes;
                if (b <= 0) return string.Empty;
                if (b < 1024) return b + " B";
                double kb = b / 1024.0;
                if (kb < 1024) return kb.ToString("0") + " KB";
                double mb = kb / 1024.0;
                if (mb < 1024) return mb.ToString("0.0") + " MB";
                return (mb / 1024.0).ToString("0.00") + " GB";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
