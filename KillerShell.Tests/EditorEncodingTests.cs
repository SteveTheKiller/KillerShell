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

namespace KillerShell.Tests
{
    /// <summary>
    /// Opens real files through the editor's own load and save. A file with no edits must save
    /// back byte for byte, and a file whose bytes do not match the encoding its byte order mark
    /// claims must be refused instead of opened with replacement characters that the next save
    /// would write over the original.
    /// </summary>
    public sealed class EditorEncodingTests
    {
        [Theory]
        [InlineData("utf8")]
        [InlineData("utf8-bom")]
        [InlineData("utf16le")]
        [InlineData("utf16be")]
        [InlineData("utf32le")]
        [InlineData("utf32be")]
        [InlineData("utf16le-leading-nul")]
        [InlineData("utf32le-mark-on-utf16")]
        [InlineData("ansi")]
        [InlineData("empty")]
        [InlineData("bom-only")]
        [InlineData("bad-utf8-bom")]
        [InlineData("bad-utf16le-odd")]
        [InlineData("bad-utf16le-lone-surrogate")]
        [InlineData("bad-utf16be")]
        [InlineData("bad-utf32le")]
        [InlineData("bad-utf32be")]
        public Task UnchangedFilesRoundTripAndMalformedFilesAreRefused(string scenario) => RunIsolated(scenario);

        [Theory]
        [InlineData("save-locked")]
        [InlineData("save-denied")]
        [InlineData("save-new-file")]
        [InlineData("save-lossy-ansi")]
        public Task FailedSavesKeepTheFileAndTheEdits(string scenario) => RunIsolated(scenario);

        [Theory]
        [InlineData("stale-external-edit")]
        [InlineData("stale-same-size")]
        [InlineData("stale-deleted")]
        [InlineData("stale-recreated-identical")]
        [InlineData("stale-overwrite")]
        [InlineData("unchanged-repeated-saves")]
        [InlineData("stale-after-own-save")]
        [InlineData("stale-denied")]
        public Task SavesNeverReplaceNewerContentOnDisk(string scenario) => RunIsolated(scenario);

        [Fact]
        public Task EncodingChangesSurviveTextUndo() => RunIsolated("encoding-undo");

        private static async Task RunIsolated(string scenario)
        {
            string assembly = typeof(EditorEncodingTests).Assembly.Location.Replace("'", "''");
            string application = typeof(MainWindow).Assembly.Location.Replace("'", "''");
            string assertions = typeof(Assert).Assembly.Location.Replace("'", "''");
            string script = "$ErrorActionPreference = 'Stop'; try { " +
                "[Reflection.Assembly]::LoadFrom('" + application + "') | Out-Null; " +
                "[Reflection.Assembly]::LoadFrom('" + assertions + "') | Out-Null; " +
                "[Reflection.Assembly]::LoadFrom('" + assembly + "') | Out-Null; " +
                "[KillerShell.Tests.EditorEncodingScenario]::Run('" + scenario + "'); exit 0 " +
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
                Assert.Fail("The editor encoding scenario did not finish.");
            }
            Assert.True(process.ExitCode == 0, $"Scenario {scenario} exited with {process.ExitCode}.\n" + await output + await errors);
        }
    }

    public static class EditorEncodingScenario
    {
        private const string Sample = "Café über 日本 \U0001F600\r\nline two\r\n";

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
            string folder = Path.Combine(Path.GetTempPath(), "ks-editor-encoding-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try { Check(scenario, Path.Combine(folder, "fixture.txt")); }
            finally
            {
                try { Directory.Delete(folder, true); } catch (IOException) { }
                app.Shutdown();
            }
        }

        private static void Check(string scenario, string path)
        {
            switch (scenario)
            {
                case "encoding-undo":
                {
                    File.WriteAllBytes(path, Encoding.UTF8.GetBytes("original"));
                    var editor = Loaded(path);
                    int dirtyChanges = 0;
                    editor.DirtyChanged += () => dirtyChanges++;
                    editor.Document.Insert(0, "edit ");
                    editor.SetEncoding(new UnicodeEncoding(false, true));
                    editor.Undo();
                    Assert.Equal("original", editor.Text);
                    Assert.True(editor.Dirty, "Undo hid the pending encoding change.");
                    editor.SetEncoding(new UTF8Encoding(false));
                    Assert.False(editor.Dirty);
                    editor.Redo();
                    Assert.True(editor.Dirty);
                    editor.Undo();
                    Assert.False(editor.Dirty);
                    editor.SetEncoding(new UTF8Encoding(true));
                    Assert.True(editor.Dirty);
                    Assert.True(editor.SaveFile(out string error, out _), error);
                    Assert.False(editor.Dirty);
                    Assert.Equal(Bytes("EF BB BF 6F 72 69 67 69 6E 61 6C"), File.ReadAllBytes(path));
                    editor.SetEncoding(new UTF8Encoding(false));
                    Assert.True(editor.Dirty);
                    editor.SetEncoding(new UTF8Encoding(true));
                    Assert.False(editor.Dirty);
                    Assert.Equal(8, dirtyChanges);
                    break;
                }
                case "save-lossy-ansi":
                {
                    byte[] original = Encoding.ASCII.GetBytes("original");
                    File.WriteAllBytes(path, original);
                    var editor = Loaded(path);
                    editor.SetEncoding(Encoding.GetEncoding(1252));
                    editor.Text = "CJK \u65e5\u672c";
                    Assert.False(editor.SaveFile(out string error, out bool denied));
                    Assert.False(denied);
                    Assert.Contains("UTF-8", error);
                    Assert.True(editor.IsModified);
                    Assert.Equal("CJK \u65e5\u672c", editor.Text);
                    Assert.Equal(original, File.ReadAllBytes(path));
                    Assert.Throws<InvalidOperationException>(() => editor.EncodedBytes());
                    editor.Text = "Caf\u00e9";
                    Assert.True(editor.SaveFile(out _, out _));
                    Assert.Equal(Bytes("43 61 66 E9"), File.ReadAllBytes(path));
                    editor.Text = "CJK \u65e5\u672c";
                    editor.SetEncoding(new UTF8Encoding(false));
                    Assert.True(editor.SaveFile(out _, out _));
                    Assert.Equal(Bytes("43 4A 4B 20 E6 97 A5 E6 9C AC"), File.ReadAllBytes(path));
                    Assert.False(editor.IsModified);
                    AssertOnlyFile(path);
                    break;
                }
                case "utf8": RoundTrip(path, new UTF8Encoding(false).GetBytes(Sample), "UTF-8", Sample); break;
                case "utf8-bom": RoundTrip(path, WithBom(new UTF8Encoding(true)), "UTF-8 BOM", Sample); break;
                case "utf16le": RoundTrip(path, WithBom(new UnicodeEncoding(false, true)), "UTF-16 LE", Sample); break;
                case "utf16be": RoundTrip(path, WithBom(new UnicodeEncoding(true, true)), "UTF-16 BE", Sample); break;
                case "utf32le": RoundTrip(path, WithBom(new UTF32Encoding(false, true)), "UTF-32 LE", Sample); break;
                case "utf32be": RoundTrip(path, WithBom(new UTF32Encoding(true, true)), "UTF-32 BE", Sample); break;
                case "utf16le-leading-nul":
                    // FF FE 00 00 also starts a UTF-32 LE mark. A UTF-16 file whose first
                    // character is NUL and whose length does not fit UTF-32 stays UTF-16.
                    RoundTrip(path, Bytes("FF FE 00 00 41 00"), "UTF-16 LE", "\0A");
                    break;
                case "ansi":
                    // Not valid UTF-8, so it is read with the system code page as before.
                    RoundTrip(path, Bytes("48 E9 6C 6C 6F 0D 0A"), "ANSI", null);
                    break;
                case "empty": RoundTrip(path, Array.Empty<byte>(), "UTF-8", ""); break;
                case "bom-only": RoundTrip(path, Bytes("EF BB BF"), "UTF-8 BOM", ""); break;
                case "bad-utf8-bom": Refused(path, Bytes("EF BB BF C3 28"), "UTF-8"); break;
                case "bad-utf16le-odd": Refused(path, Bytes("FF FE 41 00 FF"), "UTF-16 LE"); break;
                case "bad-utf16le-lone-surrogate": Refused(path, Bytes("FF FE 41 00 00 DC 42 00"), "UTF-16 LE"); break;
                case "bad-utf16be": Refused(path, Bytes("FE FF 00 41 D8 00"), "UTF-16 BE"); break;
                case "utf32le-mark-on-utf16":
                    // Starts like UTF-32 LE but is not valid UTF-32. It is valid UTF-16 LE, which
                    // reads and writes these exact bytes, so it opens as that.
                    RoundTrip(path, Bytes("FF FE 00 00 41 00 00 00 00 00 11 00"), "UTF-16 LE", "\0A\0\0\u0011");
                    break;
                case "bad-utf32le": Refused(path, Bytes("FF FE 00 00 00 00 11 00 00 DC 00 00"), "UTF-32 LE"); break;
                case "bad-utf32be": Refused(path, Bytes("00 00 FE FF 00 00 00 41 00 00 D8 00"), "UTF-32 BE"); break;
                case "save-locked":
                {
                    // A byte-range lock held elsewhere lets a truncating write open the file and
                    // then refuses the write, which used to leave the file empty.
                    byte[] original = new UTF8Encoding(false).GetBytes(Sample);
                    File.WriteAllBytes(path, original);
                    var editor = Loaded(path);
                    editor.Document.Insert(0, "edited ");
                    editor.SetEncoding(new UTF8Encoding(true));
                    using (var other = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        other.Lock(0, 4096);
                        Assert.False(editor.SaveFile(out string error, out bool denied), "Save reported success over a locked file.");
                        Assert.False(denied);
                        Assert.False(string.IsNullOrWhiteSpace(error));
                        Assert.True(editor.IsModified, "A failed save cleared the unsaved mark.");
                        editor.Undo();
                        Assert.True(editor.Dirty, "A failed save lost its pending encoding change.");
                        Assert.Equal(original.Length, new FileInfo(path).Length);   // the locked bytes cannot be read until unlocked
                        other.Unlock(0, 4096);
                    }
                    Assert.Equal(Hex(original), Hex(File.ReadAllBytes(path)));
                    AssertOnlyFile(path);
                    break;
                }
                case "save-denied":
                {
                    File.WriteAllText(path, "protected");
                    var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
                    var deny = new System.Security.AccessControl.FileSystemAccessRule(me,
                        System.Security.AccessControl.FileSystemRights.WriteData | System.Security.AccessControl.FileSystemRights.AppendData,
                        System.Security.AccessControl.AccessControlType.Deny);
                    var acl = File.GetAccessControl(path);
                    acl.AddAccessRule(deny);
                    File.SetAccessControl(path, acl);
                    try
                    {
                        var editor = Loaded(path);
                        editor.Document.Insert(0, "edited ");
                        Assert.False(editor.SaveFile(out _, out bool denied));
                        Assert.True(denied, "Access denied must still be reported so the elevated retry can be offered.");
                        Assert.True(editor.IsModified);
                        Assert.Equal("protected", File.ReadAllText(path));
                        AssertOnlyFile(path);
                    }
                    finally
                    {
                        acl = File.GetAccessControl(path);
                        acl.RemoveAccessRule(deny);
                        File.SetAccessControl(path, acl);
                    }
                    break;
                }
                case "save-new-file":
                {
                    var editor = new EditorControl(string.Empty);
                    editor.LoadEmpty();
                    editor.Text = "brand new";
                    editor.AdoptPath(path, new UTF8Encoding(false));
                    Assert.True(editor.SaveFile(out string error, out _), "Save failed: " + error);
                    Assert.False(editor.IsModified);
                    Assert.Equal("brand new", File.ReadAllText(path));
                    AssertOnlyFile(path);
                    break;
                }
                case "stale-external-edit":
                case "stale-same-size":
                {
                    // A, then another program writes B, then the stale buffer saves as C.
                    File.WriteAllText(path, "version A");
                    var editor = Loaded(path);
                    editor.Document.Insert(0, "C ");
                    string external = scenario == "stale-same-size" ? "version Z" : "version B from elsewhere";
                    File.WriteAllText(path, external);
                    Assert.False(editor.SaveFile(out _, out bool denied, out bool changed));
                    Assert.True(changed, "A newer file on disk was not reported.");
                    Assert.False(denied);
                    Assert.Equal(external, File.ReadAllText(path));
                    Assert.True(editor.Dirty, "A refused save cleared the unsaved mark.");
                    Assert.Equal("C version A", editor.Text);
                    AssertOnlyFile(path);
                    break;
                }
                case "stale-deleted":
                {
                    File.WriteAllText(path, "version A");
                    var editor = Loaded(path);
                    editor.Document.Insert(0, "C ");
                    File.Delete(path);
                    Assert.False(editor.SaveFile(out _, out _, out bool changed));
                    Assert.True(changed, "A deleted file was silently recreated.");
                    Assert.False(File.Exists(path));
                    Assert.True(editor.Dirty);
                    break;
                }
                case "stale-recreated-identical":
                {
                    // Deleted and written back with the same bytes: nothing newer exists to lose.
                    File.WriteAllText(path, "version A");
                    var editor = Loaded(path);
                    editor.Document.Insert(0, "C ");
                    File.Delete(path);
                    File.WriteAllText(path, "version A");
                    Assert.True(editor.SaveFile(out string error, out _, out bool changed), error);
                    Assert.False(changed);
                    Assert.Equal("C version A", File.ReadAllText(path));
                    break;
                }
                case "stale-overwrite":
                {
                    File.WriteAllText(path, "version A");
                    var editor = Loaded(path);
                    editor.Document.Insert(0, "C ");
                    File.WriteAllText(path, "version B");
                    Assert.False(editor.SaveFile(out _, out _, out bool changed));
                    Assert.True(changed);
                    Assert.True(editor.SaveFile(out string error, out _, out changed, overwrite: true), error);
                    Assert.False(changed);
                    Assert.Equal("C version A", File.ReadAllText(path));
                    Assert.False(editor.Dirty);
                    // The overwrite is the new baseline, so the next ordinary save just works.
                    editor.Document.Insert(0, "D ");
                    Assert.True(editor.SaveFile(out error, out _, out changed), error);
                    Assert.Equal("D C version A", File.ReadAllText(path));
                    AssertOnlyFile(path);
                    break;
                }
                case "unchanged-repeated-saves":
                {
                    File.WriteAllText(path, "version A");
                    var editor = Loaded(path);
                    for (int i = 0; i < 3; i++)
                    {
                        editor.Document.Insert(0, i + " ");
                        Assert.True(editor.SaveFile(out string error, out _, out bool changed), error);
                        Assert.False(changed);
                    }
                    Assert.Equal("2 1 0 version A", File.ReadAllText(path));
                    break;
                }
                case "stale-after-own-save":
                {
                    // The baseline moves with each save; an external write after it still counts.
                    File.WriteAllText(path, "version A");
                    var editor = Loaded(path);
                    editor.Document.Insert(0, "C ");
                    Assert.True(editor.SaveFile(out string error, out _, out _), error);
                    File.WriteAllText(path, "version B");
                    editor.Document.Insert(0, "D ");
                    Assert.False(editor.SaveFile(out _, out _, out bool changed));
                    Assert.True(changed);
                    Assert.Equal("version B", File.ReadAllText(path));
                    break;
                }
                case "stale-denied":
                {
                    // A protected file that also changed reports the conflict first, so the user
                    // decides before any elevated retry is offered.
                    File.WriteAllText(path, "version A");
                    var editor = Loaded(path);
                    editor.Document.Insert(0, "C ");
                    File.WriteAllText(path, "version B");
                    var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
                    var deny = new System.Security.AccessControl.FileSystemAccessRule(me,
                        System.Security.AccessControl.FileSystemRights.WriteData | System.Security.AccessControl.FileSystemRights.AppendData,
                        System.Security.AccessControl.AccessControlType.Deny);
                    var acl = File.GetAccessControl(path);
                    acl.AddAccessRule(deny);
                    File.SetAccessControl(path, acl);
                    try
                    {
                        Assert.False(editor.SaveFile(out _, out bool denied, out bool changed));
                        Assert.True(changed);
                        Assert.False(denied);
                        Assert.Equal("version B", File.ReadAllText(path));
                    }
                    finally
                    {
                        acl = File.GetAccessControl(path);
                        acl.RemoveAccessRule(deny);
                        File.SetAccessControl(path, acl);
                    }
                    break;
                }
                default: throw new ArgumentException("Unknown scenario " + scenario);
            }
        }

        private static EditorControl Loaded(string path)
        {
            var editor = new EditorControl(path);
            Assert.True(editor.LoadFile(out string error), "Load failed: " + error);
            return editor;
        }

        private static void AssertOnlyFile(string path) =>
            Assert.Equal(new[] { Path.GetFileName(path) }, Directory.GetFiles(Path.GetDirectoryName(path)!).Select(Path.GetFileName).ToArray());

        private static void RoundTrip(string path, byte[] original, string label, string? text)
        {
            File.WriteAllBytes(path, original);
            var editor = new EditorControl(path);
            Assert.True(editor.LoadFile(out string error), "Load failed: " + error);
            Assert.Equal(label, editor.EncodingLabel);
            if (text != null) Assert.Equal(text, editor.Text);
            Assert.True(editor.SaveFile(out string saveError, out _), "Save failed: " + saveError);
            Assert.Equal(Hex(original), Hex(File.ReadAllBytes(path)));
        }

        private static void Refused(string path, byte[] original, string label)
        {
            File.WriteAllBytes(path, original);
            var editor = new EditorControl(path);
            Assert.False(editor.LoadFile(out string error), "A malformed " + label + " file opened with text: " + Hex(Encoding.Unicode.GetBytes(editor.Text)));
            Assert.Contains(label, error);
            Assert.Equal(Hex(original), Hex(File.ReadAllBytes(path)));
        }

        private static byte[] WithBom(Encoding encoding) =>
            encoding.GetPreamble().Concat(encoding.GetBytes(Sample)).ToArray();

        private static byte[] Bytes(string hex) =>
            hex.Split(' ').Select(b => Convert.ToByte(b, 16)).ToArray();

        private static string Hex(byte[] bytes) => BitConverter.ToString(bytes);
    }
}
