using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace KillerShell.Services
{
    /// <summary>
    /// Software rendering only where it is needed, as KillerShot does (its RenderModeService
    /// Auto): remote-control tools that share the console session (ScreenConnect, Kaseya Live
    /// Connect, VNC, TeamViewer) can show a hardware-rendered WPF window as black. Everywhere
    /// else the GPU draws, because drawing the themed window on the CPU cost every click a third
    /// to half a second (measured on Trash Polka: 434 ms of CPU per click in software, 128 ms on
    /// the GPU).
    /// </summary>
    internal static class RenderModeService
    {
        // Process names of remote-control agents that capture the console session.
        private static readonly string[] RemoteAgents =
        [
            "ScreenConnect.ClientService", "ScreenConnect.WindowsClient", "AgentMon", "KaseyaRemoteControlHost",
            "TeamViewer", "TeamViewer_Service", "tvnserver", "winvnc", "vncserver", "AnyDesk", "Splashtop", "SRService",
        ];

        internal static bool RemoteControlDetected()
        {
            if (SystemParameters.IsRemoteSession) return true;
            foreach (var name in RemoteAgents)
            {
                try
                {
                    var found = Process.GetProcessesByName(name);
                    bool any = found.Length > 0;
                    foreach (var p in found) p.Dispose();
                    if (any) return true;
                }
                catch { }
            }
            return false;
        }

        /// <summary>Called at startup before any window exists.</summary>
        internal static void Apply()
        {
            RenderOptions.ProcessRenderMode = RemoteControlDetected() ? RenderMode.SoftwareOnly : RenderMode.Default;
        }
    }
}
