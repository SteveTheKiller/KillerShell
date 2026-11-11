using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using KillerShell.Editing;
using KillerShell.Services;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Editing
{
    /// <summary>
    /// Every kind of text file a person edits by hand opens in the editor, whatever its name or
    /// extension, and the address bar sends it there instead of to an association. Runs the
    /// editor's own loader in an isolated STA process, the same way EditorEncodingTests does.
    /// </summary>
    public sealed class EditorFileTypeTests
    {
        [Fact]
        public Task EveryTextFileTypeOpensInTheEditor() => RunIsolated("all-types");

        [Fact]
        public Task ALogHeldOpenForWritingOpens() => RunIsolated("held-open");

        private static async Task RunIsolated(string scenario)
        {
            string assembly = typeof(EditorFileTypeTests).Assembly.Location.Replace("'", "''");
            string application = typeof(MainWindow).Assembly.Location.Replace("'", "''");
            string assertions = typeof(Assert).Assembly.Location.Replace("'", "''");
            string script = "$ErrorActionPreference = 'Stop'; try { " +
                "[Reflection.Assembly]::LoadFrom('" + application + "') | Out-Null; " +
                "[Reflection.Assembly]::LoadFrom('" + assertions + "') | Out-Null; " +
                "[Reflection.Assembly]::LoadFrom('" + assembly + "') | Out-Null; " +
                "[KillerShell.Tests.Editing.EditorFileTypeScenario]::Run('" + scenario + "'); exit 0 " +
                "} catch { [Console]::Error.WriteLine($_.Exception.ToString()); exit 1 }";
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -STA -EncodedCommand " +
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(script)),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60000))
            {
                process.Kill();
                Assert.Fail("The editor file type scenario did not finish.");
            }
            Assert.True(process.ExitCode == 0, $"Scenario {scenario} exited with {process.ExitCode}.\n" + await output + await errors);
        }
    }

    public static class EditorFileTypeScenario
    {
        private static readonly UTF8Encoding Utf8 = new(false);

        // Names a person types or double-clicks, with the text each one usually holds. Dotfiles
        // and extensionless files have no type Windows knows, so the content is all there is.
        private static IEnumerable<(string Name, byte[] Bytes, string Text)> Cases()
        {
            (string, byte[], string) U(string name, string text) => (name, Utf8.GetBytes(text), text);

            yield return U(".env", "API_KEY=abc\nDEBUG=1\n");
            yield return U(".env.local", "PORT=3000\n");
            yield return U(".gitignore", "bin/\nobj/\n");
            yield return U(".gitattributes", "* text=auto\n");
            yield return U(".editorconfig", "root = true\n[*]\nindent_style = space\n");
            yield return U(".npmrc", "save-exact=true\n");
            yield return U(".bashrc", "export PATH=$HOME/bin:$PATH\n");
            yield return U("Dockerfile", "FROM alpine:3.20\nRUN echo hi\n");
            yield return U("Makefile", "all:\n\techo done\n");
            yield return U("LICENSE", "MIT License\n");
            yield return U("README", "Read me.\n");
            yield return U("hosts", "127.0.0.1 localhost\r\n");
            yield return U("app.json", "{ \"a\": 1 }\n");
            yield return U("tsconfig.jsonc", "{ // comment\n}\n");
            yield return U("app.xml", "<?xml version=\"1.0\"?><a/>\n");
            yield return U("web.config", "<configuration />\r\n");
            yield return U("App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />\r\n");
            yield return U("KillerShell.sln", "Microsoft Visual Studio Solution File\r\n");
            yield return U("index.html", "<!doctype html><p>hi</p>\n");
            yield return U("site.css", "body { margin: 0; }\n");
            yield return U("main.js", "console.log('hi');\n");
            yield return U("app.ts", "const a: number = 1;\n");
            yield return U("Program.cs", "class P { }\r\n");
            yield return U("main.py", "print('hi')\n");
            yield return U("run.sh", "#!/bin/sh\necho hi\n");
            yield return U("query.sql", "SELECT 1;\n");
            yield return U("Cargo.toml", "[package]\nname = \"x\"\n");
            yield return U("Cargo.lock", "version = 3\n");
            yield return U("data.csv", "a,b\n1,2\n");
            yield return U("icon.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"/>\n");
            yield return U("notes.md", "# Notes\n");
            yield return U("server.log", "2026-11-11 12:00:00 started\n");
            yield return U("script.ps1", "Write-Host hi\r\n");
            yield return U("build.bat", "@echo off\r\n");
            yield return U("id_ed25519.pub", "ssh-ed25519 AAAA test\n");
            yield return U("cert.pem", "-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n");
            yield return U("noext-unicode", "Café über 日本\n");
            yield return U("crlf.txt", "one\r\ntwo\r\n");
            yield return U("cr-only.txt", "one\rtwo\r");

            // regedit exports UTF-16 LE with a byte order mark.
            string reg = "Windows Registry Editor Version 5.00\r\n\r\n[HKEY_CURRENT_USER\\Software\\X]\r\n";
            yield return ("export.reg", new UnicodeEncoding(false, true).GetPreamble()
                .Concat(Encoding.Unicode.GetBytes(reg)).ToArray(), reg);

            // A UTF-8 file with a byte order mark, as Notepad and Visual Studio write them.
            yield return ("bom.json", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Utf8.GetBytes("{}\n")).ToArray(), "{}\n");
        }

        // Called by the isolated STA process through its public test entry point.
        public static void Run(string scenario)
        {
            var settings = new Dictionary<string, string>();
            ThemeManager.GetSetting = key => settings.TryGetValue(key, out string value) ? value : null;
            ThemeManager.SetSetting = (key, value) => settings[key] = value;
            Application.ResourceAssembly = typeof(MainWindow).Assembly;
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (string resource in new[] { "Themes/Dark.xaml", "Controls/Controls.xaml", "Controls/AppStyles.xaml", "Strings/en-US.xaml" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/KillerShell;component/" + resource)
                });
            string folder = Path.Combine(Path.GetTempPath(), "ks-editor-types-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                if (scenario == "all-types") AllTypes(folder);
                else if (scenario == "held-open") HeldOpen(folder);
                else throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
            }
            finally
            {
                try { Directory.Delete(folder, true); } catch (IOException) { }
                app.Shutdown();
            }
        }

        private static void AllTypes(string folder)
        {
            var failures = new List<string>();
            foreach (var (name, bytes, text) in Cases())
            {
                string path = Path.Combine(folder, name);
                File.WriteAllBytes(path, bytes);

                var editor = new EditorControl(path);
                if (!editor.LoadFile(out string error)) failures.Add($"{name}: load refused ({error})");
                else if (editor.Text != text) failures.Add($"{name}: text changed on load");

                // Scripts and batch files typed in the address bar run in a terminal by design.
                var expected = name.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
                            || name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                    ? AddressTargetKind.Terminal : AddressTargetKind.Edit;
                var kind = AddressTarget.Classify(path);
                if (kind != expected) failures.Add($"{name}: address bar sends it to {kind}");
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        // A log another program is still writing: the writer holds it open with write access and
        // shares reads, the way a service or a running build keeps its log.
        private static void HeldOpen(string folder)
        {
            string path = Path.Combine(folder, "service.log");
            using var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            byte[] line = Utf8.GetBytes("2026-11-11 12:00:00 running\n");
            writer.Write(line, 0, line.Length);
            writer.Flush();

            var editor = new EditorControl(path);
            Assert.True(editor.LoadFile(out string error), "Load refused: " + error);
            Assert.Equal("2026-11-11 12:00:00 running\n", editor.Text);
            Assert.Equal(AddressTargetKind.Edit, AddressTarget.Classify(path));
        }
    }
}
