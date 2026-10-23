using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using KillerShell.Terminal;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace KillerShell.Tests.Terminal;

// The console host's side of a terminal handoff, played by the test: the handles Windows'
// console host passes to ITerminalHandoff3::EstablishPtyHandoff, and the pipe ends it gets back.
public sealed class HandoffConnectionTests
{
    [Fact]
    public void ResizeTellsTheConsoleItsNewSize()
    {
        using var console = new ConsoleSide(clientExitCode: 0);
        using var connection = console.Accept();

        connection.Resize(120, 40);

        // PTY_SIGNAL_RESIZE_WINDOW (8), then width and height, each an unsigned 16-bit value
        // (microsoft/terminal src/winconpty/winconpty.cpp, _ResizePseudoConsole).
        Assert.Equal(new byte[] { 8, 0, 120, 0, 40, 0 }, console.ReadSignal(6));
    }

    [Fact]
    public void KeystrokesAndOutputCrossThePipes()
    {
        using var console = new ConsoleSide(clientExitCode: 0);
        using var connection = console.Accept();

        connection.Input.Write([0x64, 0x69, 0x72, 0x0D], 0, 4);   // "dir" Enter
        connection.Input.Flush();
        using (var fromTerminal = new FileStream(console.ConsoleInput!, FileAccess.Read, 1, isAsync: false))
        {
            var typed = new byte[4];
            Assert.Equal(4, fromTerminal.Read(typed, 0, 4));
            Assert.Equal(new byte[] { 0x64, 0x69, 0x72, 0x0D }, typed);
        }

        using (var toTerminal = new FileStream(console.ConsoleOutput!, FileAccess.Write, 1, isAsync: false))
            toTerminal.Write([0x1B, 0x5B, 0x6D, 0x68, 0x69], 0, 5);   // ESC [ m "hi"

        // The console host's end is now closed, and the terminal must hold no copy of it: that
        // EOF is how a terminal learns the console host has gone.
        var drawn = new MemoryStream();
        connection.Output.CopyTo(drawn);
        Assert.Equal(new byte[] { 0x1B, 0x5B, 0x6D, 0x68, 0x69 }, drawn.ToArray());
    }

    [Fact]
    public void ExitedReportsTheProgramsExitCode()
    {
        using var console = new ConsoleSide(clientExitCode: 7);
        using var connection = console.Accept();
        console.WaitForClientExit();

        // Subscribed only after the program has gone, which is the order a window that takes
        // a second to build will see it in.
        int code = -1;
        using var exited = new ManualResetEventSlim();
        connection.Exited += c => { code = c; exited.Set(); };

        Assert.True(exited.Wait(10000), "Exited never fired.");
        Assert.Equal(7, code);
        Assert.True(connection.HasExited);
    }

    [Fact]
    public void ProcessIdNamesTheHandedOverProgram()
    {
        using var console = new ConsoleSide(clientExitCode: 0);
        using var connection = console.Accept();

        Assert.Equal(console.ClientId, connection.ProcessId);
    }

    [Fact]
    public void ClosingTheTerminalClosesTheSignalPipe()
    {
        using var console = new ConsoleSide(clientExitCode: 0);
        var connection = console.Accept();

        connection.Dispose();

        // A broken signal pipe is how the console host learns its terminal went away, which
        // is what makes it close the program's console the way closing a console window does.
        Assert.Empty(console.ReadSignal(1));
    }
}

internal sealed class ConsoleSide : IDisposable
{
    private readonly AnonymousPipeServerStream _signal = new(PipeDirection.In);
    // Stands in for the console driver's \Reference handle, which the interface declares as a
    // file (sh_file). COM's handle passing refuses a pipe there (RPC_S_CANNOT_SUPPORT), so this
    // is an ordinary file, deleted when the test lets go of it.
    private readonly FileStream _reference = new(Path.Combine(Path.GetTempPath(), "KillerShell-handoff-" + Guid.NewGuid().ToString("N")),
                                                 FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
    private readonly Process _self = Process.GetCurrentProcess();
    private readonly Process _client;

    internal SafeFileHandle? ConsoleInput { get; private set; }
    internal SafeFileHandle? ConsoleOutput { get; private set; }

    // What the console host passes to EstablishPtyHandoff.
    internal IntPtr Signal => _signal.ClientSafePipeHandle.DangerousGetHandle();
    internal IntPtr Reference => _reference.SafeFileHandle.DangerousGetHandle();
    internal IntPtr Server => _self.Handle;
    internal IntPtr Client => _client.Handle;
    internal int ClientId => _client.Id;

    internal ConsoleSide(int clientExitCode)
    {
        _client = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = "/d /c exit " + clientExitCode,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
    }

    internal HandoffConnection Accept()
    {
        var connection = HandoffConnection.Accept(Signal, Reference, Server, Client,
                                                  out IntPtr consoleInput, out IntPtr consoleOutput);
        Adopt(consoleInput, consoleOutput);
        return connection;
    }

    /// <summary>Takes the pipe ends the terminal handed back, as the console host would.</summary>
    internal void Adopt(IntPtr consoleInput, IntPtr consoleOutput)
    {
        // The connection keeps its own copies, as it must: the console host's handles are only
        // valid for the duration of the COM call. Dropping ours is what lets the test see EOF.
        _signal.DisposeLocalCopyOfClientHandle();
        ConsoleInput = new SafeFileHandle(consoleInput, ownsHandle: true);
        ConsoleOutput = new SafeFileHandle(consoleOutput, ownsHandle: true);
    }

    internal void WaitForClientExit() => Assert.True(_client.WaitForExit(10000), "The client never exited.");

    internal byte[] ReadSignal(int count)
    {
        var bytes = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = _signal.Read(bytes, read, count - read);
            if (n == 0) break;
            read += n;
        }
        Array.Resize(ref bytes, read);
        return bytes;
    }

    public void Dispose()
    {
        ConsoleInput?.Dispose();
        ConsoleOutput?.Dispose();
        _signal.Dispose();
        _reference.Dispose();
        _self.Dispose();
        _client.Dispose();
    }
}
