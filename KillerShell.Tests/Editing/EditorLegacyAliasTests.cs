using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using KillerShell.Editing;
using KillerShell.Services;
using KillerShell.Tests.Terminal;
using Xunit;

namespace KillerShell.Tests.Editing;

public sealed class EditorLegacyAliasTests
{
    [Fact]
    public Task LegacyAliasOutsideTheStrictDecoderIsRejectedWithoutChangingTheFile()
        => EditorScenarioProcess.Run(nameof(EditorLegacyAliasScenario));
}

public static class EditorLegacyAliasScenario
{
    public static void Run()
    {
        var app = RuntimeComponentProcess.StartApplication();
        string root = Path.Combine(Path.GetTempPath(), "ks-editor-alias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            typeof(Encoding).GetField("defaultEncoding", BindingFlags.NonPublic | BindingFlags.Static)!
                .SetValue(null, Encoding.GetEncoding(932));
            string path = Path.Combine(root, "alias.txt");
            byte[] original = { 0x87, 0x90 };
            File.WriteAllBytes(path, original);
            var editor = new EditorControl(path);
            // Framework CP932's permissive fallback recognizes this alias, but its strict
            // decoder rejects it. Refusal must preserve the file instead of canonicalizing it.
            Assert.False(editor.LoadFile(out string error));
            Assert.Contains("ANSI", error);
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally
        {
            Directory.Delete(root, true);
            app.Shutdown();
        }
    }
}
