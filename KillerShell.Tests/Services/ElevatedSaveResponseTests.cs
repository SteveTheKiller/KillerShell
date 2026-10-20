using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public class ElevatedSaveResponseTests
    {
        [Fact]
        public async Task FailedHelperReturnsTheSaveDiagnosticWithoutChangingOriginal()
        {
            string root = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string target = Path.Combine(root, "protected.txt");
            try
            {
                File.WriteAllText(target, "original");
                File.SetAttributes(target, FileAttributes.ReadOnly);
                string staged = Path.Combine(root, "staged.bin");
                File.WriteAllText(staged, "replacement");
                string pipeName = "KillerShell-save-" + Guid.NewGuid().ToString("N");
                using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                Task connected = server.WaitForConnectionAsync();
                Task<string> response = Task.Run(async () =>
                {
                    await connected.ConfigureAwait(false);
                    using var header = new BinaryReader(server, Encoding.UTF8, leaveOpen: true);
                    int length = header.ReadInt32();
                    Assert.InRange(length, 1, 1024 * 1024);
                    string message = Encoding.UTF8.GetString(header.ReadBytes(length));
                    bool impersonated = ImpersonateNamedPipeClient(server.SafePipeHandle.DangerousGetHandle());
                    bool tokenOpened = false;
                    int tokenError = 0;
                    if (impersonated)
                    {
                        try
                        {
                            tokenOpened = OpenThreadToken(GetCurrentThread(), 0x0008, true, out IntPtr token);
                            tokenError = Marshal.GetLastWin32Error();
                            if (tokenOpened) CloseHandle(token);
                        }
                        finally { RevertToSelf(); }
                        // Anonymous impersonation may succeed, but Windows refuses access to its
                        // token. An unrestricted helper identity would yield a queryable token.
                        Assert.False(tokenOpened, "The diagnostic server obtained the save helper's token.");
                        Assert.Equal(1347, tokenError); // ERROR_CANT_OPEN_ANONYMOUS
                    }
                    server.WriteByte(1);
                    return message;
                });
                using var helper = Process.Start(new ProcessStartInfo(typeof(VerifiedFile).Assembly.Location)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Arguments = "--elevated-save \"" + staged + "\" \"" + target + "\" "
                        + VerifiedFile.Sha256(File.ReadAllBytes(staged)) + " " + pipeName
                })!;
                Assert.True(helper.WaitForExit(15000), "The windowless helper did not exit.");
                Assert.Equal(1, helper.ExitCode);
                Assert.Same(connected, await Task.WhenAny(connected, Task.Delay(3000)));
                await connected;
                Assert.Same(response, await Task.WhenAny(response, Task.Delay(3000)));
                string diagnostic = await response;
                Assert.Contains(target, diagnostic);
                Assert.Equal("original", File.ReadAllText(target));
            }
            finally
            {
                if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public async Task ProductionReceiverHandlesActualHelperSuccessFailureAndHashRefusal(bool readOnly, bool swapped)
        {
            string root = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string target = Path.Combine(root, "target.txt");
            try
            {
                File.WriteAllText(target, "original");
                if (readOnly) File.SetAttributes(target, FileAttributes.ReadOnly);
                string staged = Path.Combine(root, "staged.bin");
                File.WriteAllText(staged, "replacement");
                string hash = VerifiedFile.Sha256(File.ReadAllBytes(staged));
                if (swapped) File.WriteAllText(staged, "changed payload");
                using var reply = new ElevatedSaveResponse();
                using var helper = Process.Start(new ProcessStartInfo(typeof(VerifiedFile).Assembly.Location)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    Arguments = "--elevated-save \"" + staged + "\" \"" + target + "\" " + hash + " " + reply.Name
                })!;
                Assert.True(helper.WaitForExit(15000), "The windowless helper did not exit.");
                Assert.Equal(readOnly || swapped ? 1 : 0, helper.ExitCode);
                string? diagnostic = await reply.ReadAfterExitAsync();
                Assert.NotNull(diagnostic);
                if (readOnly) Assert.Contains(target, diagnostic!);
                else Assert.Empty(diagnostic!);
                Assert.Equal(readOnly || swapped ? "original" : "replacement", File.ReadAllText(target));
            }
            finally
            {
                if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public async Task ProductionChannelPreservesLongUnicodeRecoveryDiagnostic()
        {
            using var reply = new ElevatedSaveResponse();
            string diagnostic = new string('界', 90000) + " Original recovery copy: C:\\recovery\\original-ä.original";
            await Task.Run(() => ElevatedSaveResponse.Send(reply.Name, diagnostic));
            Assert.Equal(diagnostic, await reply.ReadAfterExitAsync());
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(int.MaxValue)]
        public async Task ProductionReceiverRejectsInvalidFrameLength(int length)
        {
            using var reply = new ElevatedSaveResponse();
            using var client = new NamedPipeClientStream(".", reply.Name, PipeDirection.Out,
                PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
            client.Connect(1000);
            byte[] header = BitConverter.GetBytes(length);
            await client.WriteAsync(header, 0, header.Length);
            Assert.Null(await reply.ReadAfterExitAsync());
        }

        [Fact]
        public void ClosedCallerDoesNotHoldTheDiagnosticSender()
        {
            string name;
            using (var reply = new ElevatedSaveResponse()) name = reply.Name;
            var clock = Stopwatch.StartNew();
            ElevatedSaveResponse.Send(name, "failure");
            Assert.InRange(clock.ElapsedMilliseconds, 0, 3500);
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ImpersonateNamedPipeClient(IntPtr pipe);
        [DllImport("advapi32.dll")]
        private static extern bool RevertToSelf();
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenThreadToken(IntPtr thread, uint access, bool openAsSelf, out IntPtr token);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
