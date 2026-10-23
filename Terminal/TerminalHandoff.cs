using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

// KillerShell's half of the Windows default terminal handoff.
//
// Windows' console never talks to a terminal directly. When a console program starts and the
// HKCU\Console\%%Startup pair names a delegated console host (Windows Terminal's OpenConsole)
// and a terminal, the console host serves the program's console calls itself and creates the
// terminal class named by DelegationTerminal, out of process, then calls
// ITerminalHandoff3::EstablishPtyHandoff on it (microsoft/terminal src/host/srvinit.cpp,
// ConsoleEstablishHandoff). This file is that class: KillerShell.exe started by COM registers it
// and takes the call. HandoffConnection.cs is what the call turns into.
//
// The interface is marshaled by the proxy Windows Terminal's package registers for it, the
// same proxy the console host uses on its side. No proxy is shipped here.
namespace KillerShell.Terminal
{
    /// <summary>What the console host says about the program it is handing over.</summary>
    internal sealed class HandoffStartupInfo
    {
        /// <summary>The console title: a shortcut's name, or the program's path.</summary>
        public string Title { get; private set; } = string.Empty;

        /// <summary>The icon a shortcut asked for, if any.</summary>
        public string IconPath { get; private set; } = string.Empty;

        public int IconIndex { get; private set; }

        /// <summary>The STARTUPINFO show command (SW_*) the program was started with.</summary>
        public ushort ShowWindow { get; private set; }

        /// <summary>Reads a TERMINAL_STARTUP_INFO (src/host/proxy/ITerminalHandoff.idl).</summary>
        internal static HandoffStartupInfo Read(IntPtr info)
        {
            var result = new HandoffStartupInfo();
            if (info == IntPtr.Zero) return result;
            var raw = (RawStartupInfo)Marshal.PtrToStructure(info, typeof(RawStartupInfo))!;
            result.Title = raw.Title == IntPtr.Zero ? string.Empty : Marshal.PtrToStringBSTR(raw.Title);
            result.IconPath = raw.IconPath == IntPtr.Zero ? string.Empty : Marshal.PtrToStringBSTR(raw.IconPath);
            result.IconIndex = raw.IconIndex;
            result.ShowWindow = raw.ShowWindow;
            return result;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawStartupInfo
        {
            public IntPtr Title;      // BSTR
            public IntPtr IconPath;   // BSTR
            public int IconIndex;
            public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public ushort ShowWindow;
        }
    }

    internal static class TerminalHandoff
    {
        /// <summary>
        /// KillerShell's terminal class: the value DelegationTerminal holds while KillerShell is
        /// the default terminal, and the CLSID its LocalServer32 registration is written under.
        /// Never change it; a changed CLSID strands every machine that already points at it.
        /// </summary>
        internal static readonly Guid Clsid = new("20CA035E-649B-4288-BB97-19CB0871CD62");

        /// <summary>
        /// Registers the terminal class for this process until the result is disposed.
        /// <paramref name="received"/> runs on a COM thread, once per handoff.
        /// </summary>
        /// <remarks>
        /// Single use is the product's mode: COM gives this process one handoff and starts a new
        /// KillerShell for the next, so every console program gets its own window.
        /// </remarks>
        internal static IDisposable Listen(Guid clsid, bool singleUse, Action<HandoffConnection, HandoffStartupInfo> received)
            => new Registration(clsid, singleUse, received);

        /// <summary>
        /// The class object lives in the multithreaded apartment on a thread of its own, so a
        /// handoff never waits for the UI thread: the console host is blocked until the call
        /// returns, and the window can take a second to build.
        /// </summary>
        private sealed class Registration : IDisposable
        {
            private readonly ManualResetEventSlim _stop = new();
            private readonly Thread _thread;

            internal Registration(Guid clsid, bool singleUse, Action<HandoffConnection, HandoffStartupInfo> received)
            {
                int hr = 0;
                using var ready = new ManualResetEventSlim();
                var factory = new ClassFactory(new Handoff(received));
                _thread = new Thread(() =>
                {
                    _ = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
                    hr = CoRegisterClassObject(ref clsid, factory, CLSCTX_LOCAL_SERVER,
                                               singleUse ? REGCLS_SINGLEUSE : REGCLS_MULTIPLEUSE, out uint cookie);
                    ready.Set();
                    if (hr != 0) return;
                    _stop.Wait();
                    _ = CoRevokeClassObject(cookie);
                })
                { IsBackground = true, Name = "Terminal handoff" };
                _thread.SetApartmentState(ApartmentState.MTA);
                _thread.Start();
                ready.Wait();
                if (hr != 0) throw new Win32Exception(hr, "CoRegisterClassObject");
            }

            public void Dispose()
            {
                _stop.Set();
                _thread.Join(5000);
            }
        }

        /// <remarks>
        /// StandardOleMarshalObject turns off the free-threaded marshaling every .NET object
        /// otherwise gets, so even a caller in this process goes through the real proxy and
        /// handle marshaling, exactly as the console host does from its own process.
        /// </remarks>
        private sealed class Handoff : StandardOleMarshalObject, ITerminalHandoff3
        {
            private readonly Action<HandoffConnection, HandoffStartupInfo> _received;

            internal Handoff(Action<HandoffConnection, HandoffStartupInfo> received) => _received = received;

            public int EstablishPtyHandoff(out IntPtr input, out IntPtr output, IntPtr signal, IntPtr reference,
                                           IntPtr server, IntPtr client, IntPtr startupInfo)
            {
                input = IntPtr.Zero;
                output = IntPtr.Zero;
                HandoffConnection? connection = null;
                try
                {
                    var info = HandoffStartupInfo.Read(startupInfo);
                    connection = HandoffConnection.Accept(signal, reference, server, client, out input, out output);
                    _received(connection, info);
                    return 0;
                }
                catch (Exception ex)
                {
                    // A failed handoff leaves the program in an ordinary console window: the
                    // console host gives up and Windows' own console carries on.
                    connection?.Dispose();
                    CloseIfSet(ref input);
                    CloseIfSet(ref output);
                    return ex.HResult != 0 ? ex.HResult : E_FAIL;
                }
            }

            private static void CloseIfSet(ref IntPtr handle)
            {
                if (handle != IntPtr.Zero) CloseHandle(handle);
                handle = IntPtr.Zero;
            }
        }

        private sealed class ClassFactory : IClassFactory
        {
            private readonly Handoff _handoff;

            internal ClassFactory(Handoff handoff) => _handoff = handoff;

            public int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr obj)
            {
                obj = IntPtr.Zero;
                if (outer != IntPtr.Zero) return CLASS_E_NOAGGREGATION;
                IntPtr unknown = Marshal.GetIUnknownForObject(_handoff);
                try { return Marshal.QueryInterface(unknown, ref iid, out obj); }
                finally { Marshal.Release(unknown); }
            }

            public int LockServer(bool lockServer) => 0;
        }

        // ═══════════════════════════════════════════════════════════
        //  INTEROP
        // ═══════════════════════════════════════════════════════════
        private const uint CLSCTX_LOCAL_SERVER  = 0x4;
        private const uint REGCLS_SINGLEUSE     = 0x0;
        private const uint REGCLS_MULTIPLEUSE   = 0x1;
        private const uint COINIT_MULTITHREADED = 0x0;
        private const int  CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
        private const int  E_FAIL               = unchecked((int)0x80004005);

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

        [DllImport("ole32.dll")]
        private static extern int CoRegisterClassObject(ref Guid clsid, [MarshalAs(UnmanagedType.IUnknown)] object unknown,
                                                        uint context, uint flags, out uint cookie);

        [DllImport("ole32.dll")]
        private static extern int CoRevokeClassObject(uint cookie);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }

    /// <summary>ITerminalHandoff3 from microsoft/terminal src/host/proxy/ITerminalHandoff.idl.</summary>
    /// <remarks>Public and COM-visible because COM can only reach a managed object through one.</remarks>
    [ComVisible(true), Guid("6F23DA90-15C5-4203-9DB0-64E73F1B1B00"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITerminalHandoff3
    {
        [PreserveSig]
        int EstablishPtyHandoff(out IntPtr input, out IntPtr output, IntPtr signal, IntPtr reference,
                                IntPtr server, IntPtr client, IntPtr startupInfo);
    }

    [ComVisible(true), Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IClassFactory
    {
        [PreserveSig]
        int CreateInstance(IntPtr outer, ref Guid iid, out IntPtr obj);

        [PreserveSig]
        int LockServer([MarshalAs(UnmanagedType.Bool)] bool lockServer);
    }
}
