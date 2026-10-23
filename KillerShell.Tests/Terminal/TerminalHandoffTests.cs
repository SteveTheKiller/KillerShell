using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

// The COM boundary, crossed the way Windows Terminal's console host crosses it: from another
// process, activating the class out of process and calling ITerminalHandoff3 through its vtable.
// The class object is registered for this process only and under a throwaway CLSID, so nothing
// is written to the registry. The call is marshaled by the proxy Windows Terminal's package
// registers for ITerminalHandoff3, the same proxy a real handoff uses, and its handles really
// are copied between processes. (In one process COM refuses handle marshaling outright.)
public sealed class TerminalHandoffTests
{
    [Fact]
    public async Task AConsoleHostInAnotherProcessGetsAWorkingConnection()
    {
        var clsid = Guid.NewGuid();
        HandoffConnection? received = null;
        HandoffStartupInfo? startup = null;
        using var arrived = new ManualResetEventSlim();
        using var listening = TerminalHandoff.Listen(clsid, singleUse: true, (connection, info) =>
        {
            received = connection;
            startup = info;
            arrived.Set();
        });

        string verdict = await ConsoleHostProcess.Run(clsid);

        Assert.True(arrived.IsSet, "The handoff never reached the listener. " + verdict);
        Assert.StartsWith("exit 0", verdict);
        using var connection = received!;
        Assert.Equal("KillerShell handoff test", startup!.Title);

        // The console host wrote "ok" and exited. EOF here proves the pipe end crossed COM with
        // its ownership: no copy was left in this process to hold the pipe open.
        var drawn = new MemoryStream();
        var copy = Task.Run(() => connection.Output.CopyTo(drawn));
        Assert.True(copy.Wait(10000), "The output pipe never ended.");
        Assert.Equal(new byte[] { 0x6F, 0x6B }, drawn.ToArray());
    }
}

public sealed class HandoffStartupInfoTests
{
    [Theory]
    [InlineData(1, false, true)]    // SW_SHOWNORMAL
    [InlineData(3, true, true)]     // SW_SHOWMAXIMIZED: a shortcut set to Run: Maximized
    [InlineData(4, false, false)]   // SW_SHOWNOACTIVATE
    [InlineData(8, false, false)]   // SW_SHOWNA
    public void TheWindowFollowsTheProgramsShowCommand(short show, bool maximized, bool activate)
    {
        IntPtr native = Marshal.AllocHGlobal(56);
        try
        {
            for (int i = 0; i < 56; i += 4) Marshal.WriteInt32(native, i, 0);
            Marshal.WriteInt16(native, 52, show);

            var info = HandoffStartupInfo.Read(native);

            Assert.Equal(maximized, info.StartsMaximized);
            Assert.Equal(activate, info.StartsActivated);
        }
        finally { Marshal.FreeHGlobal(native); }
    }
}

/// <summary>Plays Windows Terminal's console host in a separate, windowless process.</summary>
internal static class ConsoleHostProcess
{
    internal static async Task<string> Run(Guid clsid)
    {
        string assembly = typeof(ConsoleHostProcess).Assembly.Location.Replace("'", "''");
        string application = typeof(HandoffConnection).Assembly.Location.Replace("'", "''");
        string assertions = typeof(Assert).Assembly.Location.Replace("'", "''");
        string script = "$ErrorActionPreference='Stop'; try { " +
            "[Reflection.Assembly]::LoadFrom('" + application + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + assertions + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + assembly + "') | Out-Null; " +
            "[KillerShell.Tests.Terminal.ConsoleHostScenario]::Run('" + clsid + "'); exit 0 " +
            "} catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " +
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(35000))
        {
            process.Kill();
            Assert.Fail("The console host process exceeded its deadline.");
        }
        return "exit " + process.ExitCode + "\n" + await output + await errors;
    }
}

public static class ConsoleHostScenario
{
    public static void Run(string clsid)
    {
        using var console = new ConsoleSide(clientExitCode: 0);
        int hr = ConsoleHostCall.Establish(new Guid(clsid), console, "KillerShell handoff test",
                                           out IntPtr input, out IntPtr output);
        Assert.True(hr == 0, "EstablishPtyHandoff returned 0x" + hr.ToString("X8"));
        console.Adopt(input, output);
        using var toTerminal = new FileStream(console.ConsoleOutput!, FileAccess.Write, 1, isAsync: false);
        toTerminal.Write([0x6F, 0x6B], 0, 2);   // "ok"
    }
}

/// <summary>
/// OpenConsole's call: CoCreateInstance(CLSCTX_LOCAL_SERVER) and a raw vtable call, as native
/// code makes it, so nothing on this side can short-circuit to a managed object.
/// </summary>
internal static class ConsoleHostCall
{
    internal static int Establish(Guid clsid, ConsoleSide console, string title, out IntPtr input, out IntPtr output)
    {
        int hr = -1;
        IntPtr inPipe = IntPtr.Zero, outPipe = IntPtr.Zero;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            IntPtr startup = IntPtr.Zero, bstrTitle = IntPtr.Zero, bstrIcon = IntPtr.Zero;
            try
            {
                CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
                Guid iid = new("6F23DA90-15C5-4203-9DB0-64E73F1B1B00");   // ITerminalHandoff3
                hr = CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_LOCAL_SERVER, ref iid, out IntPtr handoff);
                if (hr != 0) return;
                try
                {
                    // TERMINAL_STARTUP_INFO, x64 layout (microsoft/terminal src/host/proxy/ITerminalHandoff.idl).
                    startup = Marshal.AllocHGlobal(56);
                    for (int i = 0; i < 56; i += 4) Marshal.WriteInt32(startup, i, 0);
                    bstrTitle = Marshal.StringToBSTR(title);
                    bstrIcon = Marshal.StringToBSTR(string.Empty);
                    Marshal.WriteIntPtr(startup, 0, bstrTitle);
                    Marshal.WriteIntPtr(startup, 8, bstrIcon);
                    Marshal.WriteInt16(startup, 52, 1);   // wShowWindow = SW_SHOWNORMAL

                    IntPtr slot = Marshal.ReadIntPtr(Marshal.ReadIntPtr(handoff), 3 * IntPtr.Size);
                    var call = (EstablishPtyHandoffCall)Marshal.GetDelegateForFunctionPointer(slot, typeof(EstablishPtyHandoffCall));
                    hr = call(handoff, out inPipe, out outPipe, console.Signal, console.Reference,
                              console.Server, console.Client, startup);
                }
                finally { Marshal.Release(handoff); }
            }
            catch (Exception e) { failure = e; }
            finally
            {
                if (bstrTitle != IntPtr.Zero) Marshal.FreeBSTR(bstrTitle);
                if (bstrIcon != IntPtr.Zero) Marshal.FreeBSTR(bstrIcon);
                if (startup != IntPtr.Zero) Marshal.FreeHGlobal(startup);
            }
        });
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        Assert.True(thread.Join(15000), "The console host call never returned.");
        if (failure != null) throw new InvalidOperationException("Console host call failed.", failure);
        input = inPipe;
        output = outPipe;
        return hr;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EstablishPtyHandoffCall(IntPtr self, out IntPtr input, out IntPtr output, IntPtr signal,
                                                 IntPtr reference, IntPtr server, IntPtr client, IntPtr startupInfo);

    private const uint CLSCTX_LOCAL_SERVER = 0x4;
    private const uint COINIT_MULTITHREADED = 0x0;

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr obj);
}
