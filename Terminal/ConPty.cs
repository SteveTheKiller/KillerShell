using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

// A real pseudoconsole, not a redirected stdout.
//
// Redirecting a shell's pipes gets you its OUTPUT but not a terminal: the shell sees no
// console, so it turns color off, prints no prompt the way you expect, and anything that
// moves the cursor (a progress bar, winget, vim, even PowerShell's own PSReadLine) has
// nowhere to draw. ConPTY is the Windows API for the other half - the child believes it is
// attached to a console of a given size, and everything it would have drawn arrives here as
// a VT stream to parse.
//
// Available since Windows 10 1809. Older boxes fail at CreatePseudoConsole, which Start
// surfaces as a clean message rather than a crash.
//
// No dependency added: this is kernel32 through P/Invoke.
namespace KillerShell.Terminal
{
    internal sealed class ConPtySession : IDisposable
    {
        private IntPtr _pc = IntPtr.Zero;          // HPCON
        private IntPtr _process = IntPtr.Zero;
        private IntPtr _thread = IntPtr.Zero;
        private SafeFileHandle? _inWrite;
        private SafeFileHandle? _outRead;
        private bool _disposed;

        /// <summary>Everything the child draws, as a VT byte stream.</summary>
        public Stream Output { get; private set; } = Stream.Null;

        /// <summary>Keystrokes, VT encoded, in.</summary>
        public Stream Input { get; private set; } = Stream.Null;

        /// <summary>Raised when the child exits, with its exit code. Fires OFF the UI thread.</summary>
        public event Action<int>? Exited;

        public bool HasExited { get; private set; }

        // ═══════════════════════════════════════════════════════════
        //  START
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Launch <paramref name="commandLine"/> attached to a new pseudoconsole.
        /// </summary>
        /// <remarks>
        /// The command line is ONE string because CreateProcess parses it itself and the
        /// quoting rules are its own; a pre-split argv would only be re-joined.
        /// </remarks>
        public static ConPtySession Start(string commandLine, string workingDir, short cols, short rows)
        {
            if (cols < 1) cols = 80;
            if (rows < 1) rows = 25;

            var s = new ConPtySession();
            SafeFileHandle? inRead = null, outWrite = null;

            try
            {
                // Two anonymous pipes. The pseudoconsole takes the ends the CHILD talks to; we
                // keep the opposite ends, which are what the streams below wrap.
                if (!CreatePipe(out inRead, out var inWrite, IntPtr.Zero, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (in)");
                if (!CreatePipe(out var outRead, out outWrite, IntPtr.Zero, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (out)");

                s._inWrite = inWrite;
                s._outRead = outRead;

                var size = new COORD { X = cols, Y = rows };
                int hr = CreatePseudoConsole(size, inRead, outWrite, 0, out s._pc);
                if (hr != 0) throw Marshal.GetExceptionForHR(hr) ?? new Win32Exception(hr);

                // OUR copies of the child's ends go now. Left open, the read below would never
                // see EOF: the pipe would still have a live writer in this process long after
                // the shell had gone.
                inRead.Dispose();   inRead = null;
                outWrite.Dispose(); outWrite = null;

                s.Launch(commandLine, workingDir);

                s.Output = new FileStream(s._outRead, FileAccess.Read, 4096, isAsync: false);
                s.Input  = new FileStream(s._inWrite, FileAccess.Write, 4096, isAsync: false);

                s.WatchForExit();
                return s;
            }
            catch
            {
                inRead?.Dispose();
                outWrite?.Dispose();
                s.Dispose();
                throw;
            }
        }

        /// <summary>
        /// CreateProcess with the pseudoconsole handed over through an attribute list. This is
        /// the whole trick: PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE is what attaches the child to
        /// our HPCON instead of to an inherited console, which a WinExe does not have.
        /// </summary>
        private void Launch(string commandLine, string workingDir)
        {
            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf(typeof(STARTUPINFOEX));

            // Called twice on purpose: the first call fails with INSUFFICIENT_BUFFER and fills
            // in the size, which is the documented way to learn it.
            IntPtr bytes = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            si.lpAttributeList = Marshal.AllocHGlobal(bytes.ToInt32());

            try
            {
                if (!InitializeProcThreadAttributeList(si.lpAttributeList, 1, 0, ref bytes))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList");

                if (!UpdateProcThreadAttribute(si.lpAttributeList, 0,
                        (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _pc, (IntPtr)IntPtr.Size,
                        IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute");

                if (string.IsNullOrEmpty(workingDir) || !Directory.Exists(workingDir))
                    workingDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                // A mutable buffer: CreateProcessW is documented as able to write into the
                // command line, and handing it a literal would corrupt the intern pool.
                var cmd = new System.Text.StringBuilder(commandLine);

                if (!CreateProcess(null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                        EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero, workingDir,
                        ref si, out var pi))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess");

                _process = pi.hProcess;
                _thread  = pi.hThread;
            }
            finally
            {
                if (si.lpAttributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(si.lpAttributeList);
                    Marshal.FreeHGlobal(si.lpAttributeList);
                }
            }
        }

        /// <summary>
        /// One background thread parked on the process handle. Cheaper than polling, and the
        /// only thing that can tell a real exit from a pipe that went quiet because the shell
        /// is simply sitting at its prompt waiting for input.
        /// </summary>
        private void WatchForExit()
        {
            var t = new Thread(() =>
            {
                WaitForSingleObject(_process, INFINITE);
                GetExitCodeProcess(_process, out int code);
                HasExited = true;
                try { Exited?.Invoke(code); } catch { /* a dying tab is not worth a crash */ }
            })
            { IsBackground = true, Name = "ConPTY exit watch" };
            t.Start();
        }

        // ═══════════════════════════════════════════════════════════
        //  RESIZE
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Tell the child its console changed size. Skip it and a resized window leaves the
        /// shell wrapping at the old width, which is the classic "why is my prompt wrapping in
        /// the middle of nowhere" bug.
        /// </summary>
        public void Resize(short cols, short rows)
        {
            if (_pc == IntPtr.Zero || _disposed) return;
            if (cols < 1) cols = 1;
            if (rows < 1) rows = 1;
            ResizePseudoConsole(_pc, new COORD { X = cols, Y = rows });
        }

        // ═══════════════════════════════════════════════════════════
        //  TEARDOWN
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Order matters. ClosePseudoConsole signals the child to end and BLOCKS until the
        /// output pipe has drained, so the read side must still be open when it runs - closing
        /// our streams first can deadlock it.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_pc != IntPtr.Zero) { ClosePseudoConsole(_pc); _pc = IntPtr.Zero; }

            try { Input.Dispose(); }  catch { }
            try { Output.Dispose(); } catch { }

            if (_thread  != IntPtr.Zero) { CloseHandle(_thread);  _thread  = IntPtr.Zero; }
            if (_process != IntPtr.Zero) { CloseHandle(_process); _process = IntPtr.Zero; }
        }

        // ═══════════════════════════════════════════════════════════
        //  INTEROP
        // ═══════════════════════════════════════════════════════════
        private const int  PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
        private const uint EXTENDED_STARTUPINFO_PRESENT        = 0x00080000;
        private const uint INFINITE                            = 0xFFFFFFFF;

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD { public short X; public short Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved, lpDesktop, lpTitle;
            public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
            public short wShowWindow, cbReserved2;
            public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess, hThread;
            public int dwProcessId, dwThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe,
                                              IntPtr lpPipeAttributes, int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput,
                                                      uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount,
                                                                     int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute,
                                                             IntPtr lpValue, IntPtr cbSize,
                                                             IntPtr lpPreviousValue, IntPtr lpReturnSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(string? lpApplicationName, System.Text.StringBuilder lpCommandLine,
                                                 IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
                                                 bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
                                                 string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo,
                                                 out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out int lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
