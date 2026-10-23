using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using KillerShell.Terminal;
using AppMainWindow = KillerShell.Shell.MainWindow;

// ═══════════════════════════════════════════════════════════════════
//  DEFAULT TERMINAL  -  the process Windows starts for one console program
// ═══════════════════════════════════════════════════════════════════
// With KillerShell as the default terminal, COM starts KillerShell.exe -Embedding for each
// console program Windows hands over (Terminal/TerminalHandoff.cs). That process registers the
// terminal class for exactly one handoff, opens one window for that program
// (Shell/ConsoleHandoff.cs), and ends when the window closes. Nothing else of a normal start runs:
// no install offers, no Run value refresh, no session.
namespace KillerShell
{
    public partial class App
    {
        private IDisposable? _handoffListener;

        /// <summary>
        /// COM appends -Embedding to a LocalServer32 command line when it starts a server to
        /// answer an activation.
        /// </summary>
        internal static bool IsComServerStart(string[] args) => Array.Exists(args, a =>
            string.Equals(a, "-Embedding", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "/Embedding", StringComparison.OrdinalIgnoreCase));

        private void StartForConsoleHandoff()
        {
            HandoffLog("started");
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // COM only starts this process because a handoff is on its way. If none arrives, the
            // console host has given up and the program is in an ordinary console window already.
            var deadline = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            deadline.Tick += (_, _) =>
            {
                deadline.Stop();
                HandoffLog("no handoff arrived");
                Shutdown(1);
            };

            try
            {
                _handoffListener = TerminalHandoff.Listen(TerminalHandoff.Clsid, singleUse: true, (connection, info) =>
                {
                    HandoffLog("received pid=" + connection.ProcessId + " title=" + info.Title);
                    connection.Exited += code => HandoffLog("exited pid=" + connection.ProcessId + " code=" + code);
                    Dispatcher.BeginInvoke(new Action(() => OpenConsoleWindow(connection, info, deadline)));
                });
            }
            catch (Exception ex)
            {
                HandoffLog("listen failed " + ex.Message);
                Shutdown(1);
                return;
            }

            Exit += (_, _) => _handoffListener?.Dispose();
            deadline.Start();
        }

        private void OpenConsoleWindow(HandoffConnection connection, HandoffStartupInfo info, DispatcherTimer deadline)
        {
            deadline.Stop();
            AppMainWindow.PendingConsole = (connection, info);
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            MainWindow = new AppMainWindow();
            MainWindow.ContentRendered += (_, _) => HandoffLog("shown pid=" + connection.ProcessId);
            MainWindow.Show();
        }

        /// <summary>
        /// Handoff timings, for the lab and for support. Written only while HKCU\Software\KillerShell
        /// HandoffLog is "1", to handoff.log beside crash.log.
        /// </summary>
        internal static void HandoffLog(string message)
        {
            if (GetSetting("HandoffLog") != "1") return;
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
                Directory.CreateDirectory(dir);
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                File.AppendAllText(Path.Combine(dir, "handoff.log"),
                    DateTime.UtcNow.ToString("o") + " " + process.Id + " " + message + Environment.NewLine);
            }
            catch { /* a diagnostic log is never worth a failed handoff */ }
        }
    }
}
