using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KillerShell.Models
{
    /// <summary>
    /// One row of the Services view of the Processes/Services tab (Shell/ProcessListControl.cs):
    /// a single Windows service, resampled on every refresh tick the same way ProcessInfo is
    /// rather than rebuilt, so a row's identity (keyed by Name, which is stable for a service's
    /// whole lifetime unlike a process's PID) survives a tick and the grid's selection/scroll
    /// position does too.
    /// </summary>
    /// <remarks>
    /// Notifying, like ProcessInfo and unlike EventLogEntryInfo - a service's Status genuinely
    /// changes while the tab is open (an admin action taken from ANOTHER tool, or this tab's own
    /// Start/Stop/Restart), and the row is edited in place on every refresh rather than replaced,
    /// so every field that can visibly change on a live machine has to raise PropertyChanged or
    /// the cells bound to it would freeze at whatever they first showed. StartupType and LogOnAs
    /// can also change (a service reconfigured from services.msc while this tab is open), so they
    /// notify too rather than only Status - the cost of doing so is one extra Notify() call on an
    /// already-cheap setter, not worth special-casing.
    /// </remarks>
    public sealed class ServiceInfo : INotifyPropertyChanged
    {
        public string Name { get; }

        public ServiceInfo(string name) => Name = name;

        private string _displayName = string.Empty;
        public string DisplayName
        {
            get => _displayName;
            set { if (_displayName != value) { _displayName = value; Notify(); } }
        }

        // "Running" / "Stopped" / "Paused" / "Start Pending" / "Stop Pending" / etc. - the English
        // ServiceControllerStatus label, not localized (same simplification LevelLabel in
        // EventViewerControl.cs makes for event levels, but noted here since a service's status
        // has no existing family precedent to match against).
        private string _status = string.Empty;
        public string Status
        {
            get => _status;
            set { if (_status != value) { _status = value; Notify(); } }
        }

        // "Automatic" / "Manual" / "Disabled" / "Automatic (Delayed Start)" - read from WMI's
        // Win32_Service.StartMode, since ServiceController itself has no StartMode property.
        private string _startupType = string.Empty;
        public string StartupType
        {
            get => _startupType;
            set { if (_startupType != value) { _startupType = value; Notify(); } }
        }

        // The account the service runs as ("LocalSystem", "NT AUTHORITY\LocalService", a domain
        // account, ...) - WMI's Win32_Service.StartName, same reason StartupType needs WMI.
        private string _logOnAs = string.Empty;
        public string LogOnAs
        {
            get => _logOnAs;
            set { if (_logOnAs != value) { _logOnAs = value; Notify(); } }
        }

        // The service's executable path - WMI's Win32_Service.PathName, often WITH arguments
        // (svchost -k netsvcs and the like), unlike ProcessInfo.Path which is a bare exe path.
        // "Open file location" below strips a leading quoted/unquoted token the same way
        // ProcessListControl.ExtractArguments does for a process's command line.
        private string _path = string.Empty;
        public string Path
        {
            get => _path;
            set { if (_path != value) { _path = value; Notify(); Notify(nameof(HasPath)); } }
        }

        public bool HasPath => Path.Length > 0;

        private string _description = string.Empty;
        public string Description
        {
            get => _description;
            set { if (_description != value) { _description = value; Notify(); } }
        }

        // ServiceController.CanStop - read once per refresh from the SAME ServiceController the
        // bulk enumeration already built (ServiceController.GetServices()), never a second
        // per-row query. Drives whether the context menu's Stop item, and the Delete-key
        // shortcut, are enabled.
        private bool _canStop;
        public bool CanStop
        {
            get => _canStop;
            set { if (_canStop != value) { _canStop = value; Notify(); } }
        }

        public bool IsRunning => Status == "Running";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void Notify([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
