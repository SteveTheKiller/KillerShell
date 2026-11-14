using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KillerShell.Editing;
using KillerShell.Services;
using KillerShell.Tests.Terminal;
using Xunit;

namespace KillerShell.Tests.Editing;

public sealed class EditorGrowingUtf8Tests
{
    [Fact]
    public Task ASharedUtf8ReadKeepsTheCompleteTextAndNeverDropsAnUnfinishedCharacter()
        => EditorScenarioProcess.Run(nameof(EditorGrowingUtf8Scenario));

    [Fact]
    public Task ASharedUtf8BomDisambiguatesAnAsciiPrefixWithAnUnfinishedCharacter()
        => EditorScenarioProcess.Run(nameof(EditorGrowingUtf8BomScenario));
}

public static class EditorGrowingUtf8BomScenario
{
    public static void Run()
    {
        System.IO.Packaging.PackUriHelper.Create(new Uri("http://localhost/"));
        var app = RuntimeComponentProcess.StartApplication();
        string root = Path.Combine(Path.GetTempPath(), "ks-growing-utf8-bom-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "growing.log");
            byte[] original = { 0xEF, 0xBB, 0xBF, 0x41, 0x42, 0x43, 0x0A, 0xE2, 0x82 };
            EditorControl editor;
            using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                writer.Write(original, 0, original.Length);
                writer.Flush(true);
                editor = new EditorControl(path);
                Assert.True(editor.LoadFile(out string error), error);
                Assert.Equal("ABC\n", editor.Text);
                Assert.Equal("UTF-8 BOM", editor.EncodingLabel);
                Assert.Equal(original, editor.EncodedBytes());
            }
            Assert.True(editor.SaveFile(out string saveError, out _), saveError);
            Assert.Equal(original, File.ReadAllBytes(path));
            Console.WriteLine("bom_ascii_preserved_sha256=" + VerifiedFile.Sha256(original));
        }
        finally
        {
            Directory.Delete(root, true);
            app.Shutdown();
        }
    }
}

public static class EditorGrowingUtf8Scenario
{
    public static void Run()
    {
        System.IO.Packaging.PackUriHelper.Create(new Uri("http://localhost/"));
        var app = RuntimeComponentProcess.StartApplication();
        string root = Path.Combine(Path.GetTempPath(), "ks-growing-utf8-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "growing.log");
            foreach (byte[] tail in new[] { new byte[] { 0xC3 }, new byte[] { 0xE2, 0x82 }, new byte[] { 0xF0, 0x9F, 0x98 } })
            {
                // Literal UTF-8 for "cafe with an accent, space, Japanese sun, newline".
                byte[] original = new byte[] { 0x63, 0x61, 0x66, 0xC3, 0xA9, 0x20, 0xE6, 0x97, 0xA5, 0x0A }.Concat(tail).ToArray();
                EditorControl editor;
                using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    writer.Write(original, 0, original.Length);
                    writer.Flush(true);
                    editor = new EditorControl(path);
                    Assert.True(editor.LoadFile(out string error), error);
                    Assert.Equal("caf\u00e9 \u65e5\n", editor.Text);
                    Assert.Equal("UTF-8", editor.EncodingLabel);
                    Assert.False(editor.Dirty);
                    Assert.Equal(original, editor.EncodedBytes());
                }
                Assert.True(editor.SaveFile(out string saveError, out _), saveError);
                Assert.Equal(original, File.ReadAllBytes(path));
                Assert.True(editor.SaveFile(out saveError, out _), saveError);
                Assert.Equal(original, File.ReadAllBytes(path));
                editor.Document.Insert(0, "edited ");
                Assert.False(editor.SaveFile(out saveError, out _));
                Assert.Contains("UTF-8", saveError);
                Assert.Equal(original, File.ReadAllBytes(path));
                editor.Undo();
                editor.SetEncoding(new UnicodeEncoding(false, true));
                Assert.False(editor.SaveFile(out saveError, out _));
                Assert.Equal(original, File.ReadAllBytes(path));
                Console.WriteLine("tail=" + tail.Length + " preserved_sha256=" + VerifiedFile.Sha256(original));
            }
        }
        finally
        {
            Directory.Delete(root, true);
            app.Shutdown();
        }
    }
}
