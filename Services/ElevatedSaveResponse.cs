using System;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace KillerShell.Services
{
    /// <summary>A private, bounded diagnostic channel back from the windowless save helper.</summary>
    internal sealed class ElevatedSaveResponse : IDisposable
    {
        private const string Prefix = "KillerShell-save-";
        private const int MaximumBytes = 1024 * 1024;
        private readonly NamedPipeServerStream _server;
        private readonly Task<string?> _response;
        internal string Name { get; } = Prefix + Guid.NewGuid().ToString("N");

        internal ElevatedSaveResponse()
        {
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,
                PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));
            _server = new NamedPipeServerStream(Name, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
            _response = ReadAsync();
        }

        internal async Task<string?> ReadAfterExitAsync()
        {
            if (await Task.WhenAny(_response, Task.Delay(2000)).ConfigureAwait(false) != _response)
                return null;
            return await _response.ConfigureAwait(false);
        }

        private async Task<string?> ReadAsync()
        {
            try
            {
                await _server.WaitForConnectionAsync().ConfigureAwait(false);
                byte[] header = new byte[4];
                if (!await ReadAllAsync(header).ConfigureAwait(false)) return null;
                int length = BitConverter.ToInt32(header, 0);
                if (length < 0 || length > MaximumBytes) return null;
                byte[] body = new byte[length];
                if (!await ReadAllAsync(body).ConfigureAwait(false)) return null;
                try { await _server.WriteAsync(new byte[] { 1 }, 0, 1).ConfigureAwait(false); }
                catch { /* The complete diagnostic is already available if the helper exited. */ }
                return Encoding.UTF8.GetString(body);
            }
            catch { return null; }
        }

        private async Task<bool> ReadAllAsync(byte[] bytes)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                int read = await _server.ReadAsync(bytes, offset, bytes.Length - offset).ConfigureAwait(false);
                if (read == 0) return false;
                offset += read;
            }
            return true;
        }

        internal static void Send(string name, string diagnostic)
            => Task.Run(() => SendCore(name, diagnostic)).GetAwaiter().GetResult();

        private static void SendCore(string name, string diagnostic)
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)
                || !Guid.TryParseExact(name.Substring(Prefix.Length), "N", out _)) return;
            try
            {
                // A replacement server must never be able to impersonate the elevated helper.
                using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
                    PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
                client.Connect(1000);
                // Keep the end, which contains emergency recovery paths, if an error is unusually long.
                if (diagnostic.Length > MaximumBytes / 4)
                    diagnostic = diagnostic.Substring(diagnostic.Length - MaximumBytes / 4);
                byte[] body = Encoding.UTF8.GetBytes(diagnostic);
                byte[] packet = new byte[body.Length + 4];
                Buffer.BlockCopy(BitConverter.GetBytes(body.Length), 0, packet, 0, 4);
                Buffer.BlockCopy(body, 0, packet, 4, body.Length);
                Task exchange = ExchangeAsync(client, packet);
                if (Task.WhenAny(exchange, Task.Delay(1500)).GetAwaiter().GetResult() == exchange)
                    exchange.GetAwaiter().GetResult();
                else _ = exchange.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            }
            catch { /* The exit code still reports failure when the caller is no longer available. */ }
        }

        private static async Task ExchangeAsync(NamedPipeClientStream client, byte[] packet)
        {
            await client.WriteAsync(packet, 0, packet.Length).ConfigureAwait(false);
            // Do not close the pipe until the parent has consumed the buffered diagnostic.
            await client.ReadAsync(new byte[1], 0, 1).ConfigureAwait(false);
        }

        public void Dispose() => _server.Dispose();
    }
}
