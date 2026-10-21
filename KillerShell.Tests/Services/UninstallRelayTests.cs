using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

public sealed class UninstallRelayTests
{
    [Fact]
    public async Task ClientRejectsRecycledServerIdentityPacket()
    {
        string name = "KillerShell-uninstall-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.None);
        using var ready = new ManualResetEventSlim();
        Exception? serverFailure = null;
        var send = new Thread(() =>
        {
            try
            {
                // Run the blocking OS accept on its own thread before launching the client.
                ready.Set();
                server.WaitForConnection();
                byte[] wrongCreation = BitConverter.GetBytes(1L);
                server.Write(wrongCreation, 0, wrongCreation.Length);
            }
            catch (Exception error) { serverFailure = error; }
        }) { IsBackground = true };
        send.Start();
        Process? child = null;
        try
        {
            Assert.True(ready.Wait(5000), "The dedicated pipe server did not start.");
            child = Start(name, Process.GetCurrentProcess().MainModule!.FileName, hold: false);
            Task<string> standardError = child.StandardError.ReadToEndAsync();
            Assert.True(send.Join(15000), "The wrong-creation packet was not delivered.");
            Assert.Null(serverFailure);
            Assert.True(child.WaitForExit(15000));
            Assert.NotEqual(0, child.ExitCode);
            Assert.Contains("The uninstall coordinator process identity changed.", await standardError);
        }
        finally
        {
            if (child != null) { Stop(child); child.Dispose(); }
            server.Dispose();
            Assert.True(send.Join(5000), "The dedicated pipe server did not stop.");
        }
    }

    [Fact]
    public void UnresponsiveActualChildCannotHoldAuthenticationIndefinitely()
    {
        using var relay = new UninstallRelay();
        using var child = Launch("$p=[IO.Pipes.NamedPipeClientStream]::new('.', '" + relay.Name + "', [IO.Pipes.PipeDirection]::InOut); $p.Connect(5000); $b=New-Object byte[] 8; $null=$p.Read($b,0,8); [Threading.Thread]::Sleep(30000)");
        try
        {
            var watch = Stopwatch.StartNew();
            Assert.False(relay.AuthenticateChild(child));
            Assert.Contains("deadline", relay.Error);
            Assert.InRange(watch.Elapsed.TotalSeconds, 8, 20);
        }
        finally {Stop(child);}
    }

    [Fact]
    public void RealLaunchedPeerAuthenticatesItsHeldCoordinator()
    {
        using var relay = new UninstallRelay();
        using var child = Start(relay.Name, Process.GetCurrentProcess().MainModule!.FileName, hold: true);
        try
        {
            Assert.True(relay.AuthenticateChild(child), relay.Error);
            child.StandardInput.WriteLine();
            Assert.True(child.WaitForExit(15000));
            Assert.Equal(0, child.ExitCode);
        }
        finally { Stop(child); }
    }

    [Fact]
    public void DifferentActualPipeClientCannotImpersonateLaunchedChild()
    {
        using var relay = new UninstallRelay();
        using var expected = Waiter();
        using var impostor = Start(relay.Name, Process.GetCurrentProcess().MainModule!.FileName, hold: false);
        try
        {
            Assert.False(relay.AuthenticateChild(expected));
            Assert.Contains("not the launched child", relay.Error);
            Assert.True(impostor.WaitForExit(15000));
            Assert.NotEqual(0, impostor.ExitCode);
        }
        finally { Stop(expected); Stop(impostor); }
    }

    [Fact]
    public void ClientRejectsWrongExpectedCoordinatorImage()
    {
        using var relay = new UninstallRelay();
        using var child = Start(relay.Name, @"C:\Windows\not-the-coordinator.exe", hold: false);
        try
        {
            Assert.False(relay.AuthenticateChild(child));
            Assert.True(child.WaitForExit(15000));
            Assert.NotEqual(0, child.ExitCode);
        }
        finally { Stop(child); }
    }

    [Fact]
    public void AuthenticatedChildMayExitImmediatelyAfterAcknowledgement()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var relay = new UninstallRelay();
            using var child = Start(relay.Name, Process.GetCurrentProcess().MainModule!.FileName, hold: false);
            try
            {
                Assert.True(relay.AuthenticateChild(child), relay.Error);
                Assert.True(child.WaitForExit(15000));
                Assert.Equal(0, child.ExitCode);
            }
            finally { Stop(child); }
        }
    }

    private static Process Start(string name, string expectedImage, bool hold)
    {
        string app = typeof(UninstallRelay).Assembly.Location.Replace("'", "''");
        string test = typeof(UninstallRelayTests).Assembly.Location.Replace("'", "''");
        string script = "$ErrorActionPreference='Stop'; try { [Reflection.Assembly]::LoadFrom('" + app + "') | Out-Null; "
            + "[Reflection.Assembly]::LoadFrom('" + test + "') | Out-Null; "
            + "[KillerShell.Tests.Services.UninstallRelayScenario]::Connect('" + name + "','" + expectedImage.Replace("'", "''") + "', $" + hold.ToString().ToLowerInvariant()
            + "); exit 0 } catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }";
        return Launch(script);
    }
    private static Process Waiter() => Launch("[Console]::ReadLine() | Out-Null");
    private static Process Launch(string script) => Process.Start(new ProcessStartInfo
    {
        FileName = "powershell.exe", Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
    })!;
    private static void Stop(Process process)
    {
        if (!process.HasExited) { process.Kill(); process.WaitForExit(); }
    }
}

public static class UninstallRelayScenario
{
    public static void Connect(string name, string image, bool hold)
    {
        using var coordinator = UninstallRelay.ConnectCoordinator(name, image);
        if (hold) Console.ReadLine();
    }
}
