using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

// A console Windows handed to KillerShell instead of opening its own window for it.
//
// When KillerShell is the default terminal, starting a console program (cmd.exe from Win+R, a
// .bat from Explorer) goes: Windows' console host -> the delegated console host (Windows
// Terminal's OpenConsole) -> ITerminalHandoff3::EstablishPtyHandoff here (TerminalHandoff.cs).
// That call hands over the same pieces CreatePseudoConsole hands ConPtySession, minus the
// HPCON: a signal pipe the console host listens on, a reference handle that keeps the console
// alive, the console host process and the program itself. The terminal makes the two text
// pipes and passes their far ends back.
//
// Everything the console host passes in is only valid for the length of that COM call, so
// Accept duplicates what it keeps.
namespace KillerShell.Terminal
{
    internal sealed class HandoffConnection : ITerminalConnection
    {
        private SafeFileHandle? _signal;
        private SafeFileHandle? _reference;
        private SafeFileHandle? _server;
        private SafeFileHandle? _client;
        private FileStream? _signalStream;
        private bool _disposed;

        private readonly object _exitGate = new();
        private Action<int>? _exited;
        private int _exitCode;

        public Stream Output { get; private set; } = Stream.Null;
        public Stream Input { get; private set; } = Stream.Null;
        public bool HasExited { get; private set; }

        /// <summary>The program that was started, as Task Manager shows it.</summary>
        public int ProcessId { get; private set; }

        /// <summary>
        /// Raised once, off the UI thread, when the handed-off program exits. A handler added
        /// after that is called at once: the program can finish before its window is built.
        /// </summary>
        public event Action<int>? Exited
        {
            add
            {
                bool already;
                lock (_exitGate)
                {
                    already = HasExited;
                    if (!already) _exited += value;
                }
                if (already) value?.Invoke(_exitCode);
            }
            remove { lock (_exitGate) _exited -= value; }
        }

        private HandoffConnection() { }

        /// <summary>
        /// Takes over a handed-off console. <paramref name="consoleInput"/> and
        /// <paramref name="consoleOutput"/> are the console host's ends of the two text pipes;
        /// ownership passes to the caller, which hands them back through the COM call.
        /// </summary>
        internal static HandoffConnection Accept(IntPtr signal, IntPtr reference, IntPtr server, IntPtr client,
                                                 out IntPtr consoleInput, out IntPtr consoleOutput)
        {
            var c = new HandoffConnection();
            SafeFileHandle? inRead = null, inWrite = null, outRead = null, outWrite = null;
            try
            {
                c._signal    = Duplicate(signal);
                c._reference = Duplicate(reference);
                c._server    = Duplicate(server);
                c._client    = Duplicate(client);
                c.ProcessId  = GetProcessId(c._client);

                if (!CreatePipe(out inRead, out inWrite, IntPtr.Zero, PipeSize))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (in)");
                if (!CreatePipe(out outRead, out outWrite, IntPtr.Zero, PipeSize))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (out)");

                c._signalStream = new FileStream(c._signal, FileAccess.Write, 1, isAsync: false);
                c.Input  = new FileStream(inWrite, FileAccess.Write, 4096, isAsync: false);
                c.Output = new FileStream(outRead, FileAccess.Read, 4096, isAsync: false);

                consoleInput  = Release(inRead);
                consoleOutput = Release(outWrite);
                c.WatchForExit();
                return c;
            }
            catch
            {
                inRead?.Dispose(); inWrite?.Dispose(); outRead?.Dispose(); outWrite?.Dispose();
                c.Dispose();
                throw;
            }
        }

        public void Resize(short cols, short rows)
        {
            if (_disposed || _signalStream == null) return;
            if (cols < 1) cols = 1;
            if (rows < 1) rows = 1;
            byte[] packet =
            [
                ResizeSignal, 0,
                (byte)cols, (byte)(cols >> 8),
                (byte)rows, (byte)(rows >> 8),
            ];
            try { _signalStream.Write(packet, 0, packet.Length); _signalStream.Flush(); }
            catch (IOException) { /* the console host is gone; the exit watch reports it */ }

            // The reference handle only has to hold the console open until the terminal is
            // ready, which the first size means. Kept any longer, the console host would outlive
            // the program and the output pipe would never end. Windows Terminal lets go at the
            // same point (ConptyConnection::Start, ConptyReleasePseudoConsole).
            _reference?.Dispose();
            _reference = null;
        }

        /// <summary>
        /// The program that was started, not the console host: its exit is what a console
        /// window reports, and the console host can outlive it briefly while output drains.
        /// </summary>
        private void WatchForExit()
        {
            var client = _client!;
            var t = new Thread(() =>
            {
                Action<int>? handlers;
                using (var wait = new ProcessWait(client))
                {
                    wait.WaitOne();
                    lock (_exitGate)
                    {
                        _exitCode = GetExitCodeProcess(client, out int code) ? code : -1;
                        HasExited = true;
                        handlers = _exited;
                        _exited = null;
                    }
                }
                try { handlers?.Invoke(_exitCode); } catch { /* a dying tab is not worth a crash */ }
            })
            { IsBackground = true, Name = "Handoff exit watch" };
            t.Start();
        }

        /// <summary>Lets WaitOne block on a process handle without taking ownership of it.</summary>
        private sealed class ProcessWait : WaitHandle
        {
            internal ProcessWait(SafeFileHandle process)
            {
                bool added = false;
                process.DangerousAddRef(ref added);
                _process = process;
                SafeWaitHandle = new SafeWaitHandle(process.DangerousGetHandle(), ownsHandle: false);
            }

            private readonly SafeFileHandle _process;

            protected override void Dispose(bool explicitDisposing)
            {
                base.Dispose(explicitDisposing);
                _process.DangerousRelease();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _signalStream?.Dispose(); } catch { }
            _signal?.Dispose();
            _reference?.Dispose();
            try { Input.Dispose(); }  catch { }
            try { Output.Dispose(); } catch { }
            _server?.Dispose();
            _client?.Dispose();
        }

        // ═══════════════════════════════════════════════════════════
        //  INTEROP
        // ═══════════════════════════════════════════════════════════
        // PTY signal ids from microsoft/terminal src/host/PtySignalInputThread.hpp.
        private const byte ResizeSignal = 8;

        // The size Windows Terminal gives its handoff pipes. Output written before the window
        // exists waits here, so a program that prints at once does not stall on a 4 KB pipe.
        private const int PipeSize = 128 * 1024;

        private const uint DUPLICATE_SAME_ACCESS = 0x2;

        private static SafeFileHandle Duplicate(IntPtr handle)
        {
            if (!DuplicateHandle(GetCurrentProcess(), handle, GetCurrentProcess(), out var copy,
                                 0, false, DUPLICATE_SAME_ACCESS))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateHandle");
            return copy;
        }

        private static IntPtr Release(SafeFileHandle handle)
        {
            IntPtr raw = handle.DangerousGetHandle();
            handle.SetHandleAsInvalid();
            return raw;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe,
                                              IntPtr lpPipeAttributes, int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(IntPtr hSourceProcessHandle, IntPtr hSourceHandle,
                                                   IntPtr hTargetProcessHandle, out SafeFileHandle lpTargetHandle,
                                                   uint dwDesiredAccess, bool bInheritHandle, uint dwOptions);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(SafeFileHandle hProcess, out int lpExitCode);

        [DllImport("kernel32.dll")]
        private static extern int GetProcessId(SafeFileHandle process);
    }
}
