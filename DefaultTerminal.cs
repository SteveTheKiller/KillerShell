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
            if (info.StartsMaximized) MainWindow.WindowState = WindowState.Maximized;
            MainWindow.ShowActivated = info.StartsActivated;
            MainWindow.Show();
        }

        /// <summary>The About card's default terminal setting, for this copy of KillerShell.</summary>
        internal static Services.DefaultTerminalSetting DefaultTerminalSetting()
            => new(Services.WindowsRegistryStore.Instance, installed: !IsPortable(), exePath: ExePath);

        /// <summary>
        /// Registers the terminal class for an install. It does nothing until the About card turns
        /// the default terminal on, and never fails an install: turning it on registers it again.
        /// </summary>
        private static void RegisterTerminalClass(bool machine, string exe)
        {
            try { Services.DefaultTerminalSetting.RegisterClass(Services.WindowsRegistryStore.Instance, machine, exe); }
            catch { }
        }

        /// <summary>
        /// Uninstall: puts back the default terminal the About card replaced, then removes the
        /// class. A console program started afterwards by another account still pointing here
        /// finds no class and stays in Windows' own console.
        /// </summary>
        private static void RemoveTerminalClass(bool machine, bool restoreDefault)
        {
            try { if (restoreDefault) DefaultTerminalSetting().TurnOff(); } catch { }
            try { Services.DefaultTerminalSetting.UnregisterClass(Services.WindowsRegistryStore.Instance, machine); } catch { }
        }

        /// <summary>
        /// An install from before the class existed gets it on its next start. All-users installs
        /// need an elevated start for the HKLM write, which is the only registration elevated
        /// console programs can use.
        /// </summary>
        private static void RefreshTerminalClass()
        {
            string exe = ExePath;
            bool machine = string.Equals(exe, MachineInstallExe, StringComparison.OrdinalIgnoreCase);
            if (!machine && !string.Equals(exe, InstallExe, StringComparison.OrdinalIgnoreCase)) return;
            if (machine && !AppMainWindow.IsElevated) return;

            var store = Services.WindowsRegistryStore.Instance;
            var hive = machine ? Microsoft.Win32.RegistryHive.LocalMachine : Microsoft.Win32.RegistryHive.CurrentUser;
            string server = store.GetString(hive, @"Software\Classes\CLSID\" + Services.DefaultTerminalSetting.ClassId + @"\LocalServer32", null) ?? string.Empty;
            if (!string.Equals(server, "\"" + exe + "\"", StringComparison.OrdinalIgnoreCase))
                RegisterTerminalClass(machine, exe);
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
