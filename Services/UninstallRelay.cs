using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace KillerShell.Services
{
    /// <summary>Authenticates the exact elevated child and its waiting uninstall coordinator.</summary>
    internal sealed class UninstallRelay : IDisposable
    {
        private const string Prefix = "KillerShell-uninstall-";
        private const int TimeoutMilliseconds = 10000;
        private readonly NamedPipeServerStream _server;
        private readonly Process _coordinator = Process.GetCurrentProcess();
        private int _claimed;
        internal string Name { get; } = Prefix + Guid.NewGuid().ToString("N");
        internal string? Error { get; private set; }

        internal UninstallRelay()
        {
            _ = _coordinator.Handle;
            var security = new PipeSecurity();
            using var identity = WindowsIdentity.GetCurrent();
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            _server = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous, 4096, 4096, security);
        }

        internal bool AuthenticateChild(Process child)
        {
            if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0) return false;
            try
            {
                _ = child.Handle;
                long created = child.StartTime.ToUniversalTime().Ticks;
                return AuthenticateAsync(child, created).GetAwaiter().GetResult();
            }
            catch (Exception error)
            {
                Error = error.Message;
                _server.Dispose();
                return false;
            }
        }

        private async Task<bool> AuthenticateAsync(Process child, long created)
        {
            await Bounded(_server.WaitForConnectionAsync()).ConfigureAwait(false);
            if (!GetNamedPipeClientProcessId(_server.SafePipeHandle, out uint clientId)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (clientId != child.Id || child.HasExited || child.StartTime.ToUniversalTime().Ticks != created)
                throw new SecurityException("The uninstall relay client is not the launched child.");
            byte[] creation = BitConverter.GetBytes(_coordinator.StartTime.ToUniversalTime().Ticks);
            await Bounded(_server.WriteAsync(creation, 0, creation.Length)).ConfigureAwait(false);
            byte[] acknowledgement = new byte[1];
            await ReadExactly(_server, acknowledgement).ConfigureAwait(false);
            if (acknowledgement[0] != 1) throw new SecurityException("The uninstall child did not authenticate its coordinator.");
            return true;
        }

        internal static Process ConnectCoordinator(string name, string expectedMachineExe)
            => ConnectAsync(name, expectedMachineExe).GetAwaiter().GetResult();

        private static async Task<Process> ConnectAsync(string name, string expectedMachineExe)
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name.Substring(Prefix.Length), "N", out _))
                throw new SecurityException("Invalid uninstall relay name.");
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
            await Bounded(client.ConnectAsync(TimeoutMilliseconds)).ConfigureAwait(false);
            if (!GetNamedPipeServerProcessId(client.SafePipeHandle, out uint serverId)) throw new Win32Exception(Marshal.GetLastWin32Error());
            Process coordinator = Process.GetProcessById(checked((int)serverId));
            try
            {
                _ = coordinator.Handle;
                long created = coordinator.StartTime.ToUniversalTime().Ticks;
                string actual = coordinator.MainModule?.FileName ?? "";
                if (coordinator.HasExited || !string.Equals(Path.GetFullPath(actual), Path.GetFullPath(expectedMachineExe), StringComparison.OrdinalIgnoreCase))
                    throw new SecurityException("The uninstall relay server image is not the trusted coordinator.");
                byte[] creation = new byte[8];
                await ReadExactly(client, creation).ConfigureAwait(false);
                if (BitConverter.ToInt64(creation, 0) != created || coordinator.HasExited)
                    throw new SecurityException("The uninstall coordinator process identity changed.");
                await Bounded(client.WriteAsync(new byte[] {1}, 0, 1)).ConfigureAwait(false);
                return coordinator;
            }
            catch { coordinator.Dispose(); throw; }
        }

        private static async Task ReadExactly(Stream stream, byte[] bytes)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                var read = stream.ReadAsync(bytes, offset, bytes.Length - offset);
                await Bounded(read).ConfigureAwait(false);
                int count = await read.ConfigureAwait(false);
                if (count == 0) throw new SecurityException("The uninstall relay closed before authentication completed.");
                offset += count;
            }
        }

        private static async Task Bounded(Task task)
        {
            if (await Task.WhenAny(task, Task.Delay(TimeoutMilliseconds)).ConfigureAwait(false) != task)
            {
                _ = task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                throw new TimeoutException("The uninstall relay authentication deadline expired.");
            }
            await task.ConfigureAwait(false);
        }

        public void Dispose() { _server.Dispose(); _coordinator.Dispose(); }
        [DllImport("kernel32.dll", SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
        [DllImport("kernel32.dll", SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
    }
}
