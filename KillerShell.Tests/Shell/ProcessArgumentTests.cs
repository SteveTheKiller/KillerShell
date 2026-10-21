using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KillerShell.Shell;
using Microsoft.CSharp;
using Xunit;

namespace KillerShell.Tests.Shell;

public sealed class ProcessArgumentTests
{
    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"\\server\share\")]
    [InlineData(@"C:\ordinary")]
    [InlineData(@"C:\folder with spaces\")]
    [InlineData("/home/user/a\"b")]
    [InlineData("/home/user/\"quoted\" name")]
    [InlineData("/home/user/trailing  ")]
    [InlineData("/home/user/back\\slash\\")]
    [InlineData("/home/user/caf\u00e9 \U0001f4c1")]
    [InlineData("/home/user/ends with quote\"")]
    public async Task RelaunchTransportPreservesRealProcessArguments(string folder)
    {
        string root = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", "arguments", Guid.NewGuid().ToString("N"));
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(root);
        try
        {
            string exe = Path.Combine(root, "Argv.exe");
            using (var compiler = new CSharpCodeProvider())
            {
                var options = new CompilerParameters(new[] { "System.dll" }, exe) { GenerateExecutable = true };
                var result = compiler.CompileAssemblyFromSource(options,
                    "using System; using System.Text; class Entry { static void Main(string[] args) { foreach (var a in args) Console.WriteLine(Convert.ToBase64String(Encoding.UTF8.GetBytes(a))); } }");
                Assert.False(result.Errors.HasErrors, string.Join(Environment.NewLine, result.Errors.Cast<CompilerError>()));
            }
            string handoff = "--terminal-profile fixture --cwd " + MainWindow.QuoteToken(folder);
            Assert.Equal(folder, MainWindow.TokenizeArgs(handoff).Last());
            using var child = Process.Start(new ProcessStartInfo(exe, MainWindow.ProcessRelaunchArgs(handoff))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();
            Assert.True(child.WaitForExit(10000), "Argument child did not exit.");
            Assert.True(child.ExitCode == 0, await errors);
            string[] actual = (await output).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => Encoding.UTF8.GetString(Convert.FromBase64String(s))).ToArray();
            Assert.Equal(new[] { "--terminal-profile", "fixture", "--cwd", folder }, actual);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("--cwd \"C:\\old session\"", new[] { "--cwd", "C:\\old session" })]
    [InlineData("--cwd \"\" --processes", new[] { "--cwd", "", "--processes" })]
    [InlineData("--edit \"C:\\a.txt\" --cwd \"/home/x\"", new[] { "--edit", "C:\\a.txt", "--cwd", "/home/x" })]
    public void SessionsSavedBeforeQuoteDoublingStillRead(string saved, string[] expected)
        => Assert.Equal(expected, MainWindow.TokenizeArgs(saved));
}
