using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Terminal;

public sealed class ConPtyTests
{
    [Fact]
    public async Task RedirectedHostKeepsShellInputAndOutputInsidePseudoConsole()
    {
        string assembly = typeof(ConPtyTests).Assembly.Location.Replace("'", "''");
        string application = typeof(ConPtySession).Assembly.Location.Replace("'", "''");
        string assertions = typeof(Assert).Assembly.Location.Replace("'", "''");
        string script = "$ErrorActionPreference='Stop'; try { " +
            "[Reflection.Assembly]::LoadFrom('" + application + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + assertions + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + assembly + "') | Out-Null; " +
            "[KillerShell.Tests.Terminal.ConPtyScenario]::Run(); exit 0 " +
            "} catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " +
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(35000))
        {
            process.Kill();
            Assert.Fail("The redirected-host ConPTY scenario exceeded its deadline.");
        }
        Task drained = Task.WhenAll(output, errors);
        Assert.True(await Task.WhenAny(drained, Task.Delay(5000)) == drained,
            "The redirected-host output pipes did not close after process exit.");
        Assert.True(process.ExitCode == 0, "ConPTY scenario failed.\n" + await output + await errors);
    }
}

public static class ConPtyScenario
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShell-ConPTY-" + Guid.NewGuid().ToString("N"), "unicode-\u65e5\u672c");
        Directory.CreateDirectory(root);
        try
        {
            using var session = ConPtySession.Start(Path.Combine(Environment.SystemDirectory, "cmd.exe") + " /D /Q /K", root, 80, 25);
            using var exited = new ManualResetEventSlim();
            int exitCode = -1;
            session.Exited += code => { exitCode = code; exited.Set(); };
            var captured = new MemoryStream();
            Exception? readError = null;
            bool disposing = false;
            var reader = new Thread(() =>
            {
                try
                {
                    var buffer = new byte[4096];
                    int count;
                    while ((count = session.Output.Read(buffer, 0, buffer.Length)) > 0)
                        lock (captured) captured.Write(buffer, 0, count);
                }
                catch (ObjectDisposedException) when (Volatile.Read(ref disposing)) { }
                catch (Exception error) { readError = error; }
            }) { IsBackground = true };
            string Text() { lock (captured) return Encoding.UTF8.GetString(captured.ToArray()); }
            void Send(string command)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(command + "\r\n");
                session.Input.Write(bytes, 0, bytes.Length);
                session.Input.Flush();
            }
            reader.Start();
            // Leave room for wide characters in the directory and for echoed commands.
            short columns = checked((short)Math.Max(100, root.Length * 2 + 40));
            session.Resize(columns, 30);
            // The literal marker is absent from input, so terminal input echo cannot pass this.
            Send("echo KS_CONPTY_MA^RKER");
            Send("cd");
            bool received = SpinWait.SpinUntil(() => Text().Contains("KS_CONPTY_MARKER") && Text().Contains(root), 10000);
            Send("exit 23");
            Assert.True(exited.Wait(10000), "The shell did not exit after its input command.");
            // ClosePseudoConsole drains while the reader is still alive. Keep disposal bounded
            // by the outer subprocess deadline, and require the reader to finish afterward.
            Volatile.Write(ref disposing, true);
            session.Dispose();
            Assert.True(reader.Join(3000), "The ConPTY output reader did not finish after disposal.");
            Assert.True(received, "Shell output or Unicode working directory escaped the PTY. Captured: " + Text()
                + "\nExpected root (ASCII escaped): " + Escape(root)
                + "; UTF-16 length=" + root.Length + "; resized columns=" + columns
                + "; fixture stderr code page=" + Console.Error.Encoding.CodePage
                + "\nCaptured (ASCII escaped): " + Escape(Text()));
            Assert.Null(readError);
            Assert.Equal(23, exitCode);
        }
        finally
        {
            // Failed native launches may briefly retain the working directory. Cleanup must
            // not replace the assertion that explains which terminal boundary failed.
            try { Directory.Delete(Path.GetDirectoryName(root)!, true); } catch (IOException) { }
        }
    }

    private static string Escape(string text)
    {
        var escaped = new StringBuilder();
        foreach (char character in text)
        {
            if (character == '\\') escaped.Append("\\\\");
            else if (character < ' ' || character > '~') escaped.Append("\\u").Append(((int)character).ToString("X4"));
            else escaped.Append(character);
        }
        return escaped.ToString();
    }
}
