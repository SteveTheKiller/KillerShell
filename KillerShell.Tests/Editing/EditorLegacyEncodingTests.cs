using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using KillerShell.Editing;
using KillerShell.Services;
using KillerShell.Tests.Terminal;
using Xunit;

namespace KillerShell.Tests.Editing;

public sealed class EditorLegacyEncodingTests
{
    [Fact]
    public Task MalformedLegacyInputIsRefusedBeforeItCanBeSaved()
        => EditorScenarioProcess.Run(nameof(EditorLegacyEncodingScenario));
}

internal static class EditorScenarioProcess
{
    internal static async Task Run(string scenario, string? argument = null)
    {
        string Quote(string value) => value.Replace("'", "''");
        string script = "$ErrorActionPreference='Stop'; try { " +
            "[Reflection.Assembly]::LoadFrom('" + Quote(typeof(EditorControl).Assembly.Location) + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + Quote(typeof(Assert).Assembly.Location) + "') | Out-Null; " +
            "[Reflection.Assembly]::LoadFrom('" + Quote(typeof(EditorScenarioProcess).Assembly.Location) + "') | Out-Null; " +
            "[KillerShell.Tests.Editing." + scenario + "]::Run(" +
            (argument == null ? "" : "'" + Quote(argument) + "'") + "); exit 0 " +
            "} catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }";
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -STA -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60000)) { process.Kill(); Assert.Fail("The editor scenario exceeded its deadline."); }
        Assert.True(process.ExitCode == 0, await output + await errors);
    }
}

public static class EditorLegacyEncodingScenario
{
    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        string root = Path.Combine(Path.GetTempPath(), "ks-editor-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Substitute only the OS code-page boundary in this disposable subprocess.
            // CurrentCulture does not change the Windows ANSI code page.
            var defaultEncoding = typeof(Encoding).GetField("defaultEncoding", BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.NotNull(defaultEncoding);
            defaultEncoding.SetValue(null, Encoding.GetEncoding(932));
            Assert.Equal(932, Encoding.Default.CodePage);

            string path = Path.Combine(root, "malformed.txt");
            byte[] original = { 0x81 };
            File.WriteAllBytes(path, original);
            var editor = new EditorControl(path);
            bool loaded = editor.LoadFile(out string error);
            if (loaded) Assert.True(editor.SaveFile(out _, out _));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.False(loaded, "A malformed CP932 file must be refused before an unchanged save is possible.");
            Assert.Contains("ANSI", error);
            Console.WriteLine("malformed_original_sha256=" + VerifiedFile.Sha256(original));
            Console.WriteLine("malformed_final_sha256=" + VerifiedFile.Sha256(File.ReadAllBytes(path)));

            byte[] valid = { 0x93, 0xFA, 0x96, 0x7B };
            File.WriteAllBytes(path, valid);
            var validEditor = new EditorControl(path);
            Assert.True(validEditor.LoadFile(out error), error);
            Assert.Equal("\u65e5\u672c", validEditor.Text);
            Assert.Equal("ANSI", validEditor.EncodingLabel);
            Assert.True(validEditor.SaveFile(out error, out _), error);
            Assert.Equal(valid, File.ReadAllBytes(path));
            Console.WriteLine("valid_original_sha256=" + VerifiedFile.Sha256(valid));
            Console.WriteLine("valid_final_sha256=" + VerifiedFile.Sha256(File.ReadAllBytes(path)));
        }
        finally
        {
            Directory.Delete(root, true);
            app.Shutdown();
        }
    }
}
