using System;
using System.Collections.Generic;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

[Collection(KillerShell.Tests.Terminal.NativeAccessibilityCollection.Name)]
[Trait("Category", "Window")]
public sealed class ShellMenuSelectionTests
{
    [Fact]
    public void SameFolderSelectionReachesWindowsTogether()
    {
        using var native = new RecordingSession();
        Assert.True(ShellContextMenu.Show((IntPtr)1, new[] { @"C:\one\a.txt", @"C:\one\b.txt" }, native));
        Assert.Equal(new[] { @"C:\one\a.txt", @"C:\one\b.txt" }, native.Paths);
        Assert.Equal(1, native.MenusShown);
    }

    [Fact]
    public void MixedFolderSelectionIsRefusedBeforeAnyNativeMenu()
    {
        using var native = new RecordingSession();
        Assert.False(ShellContextMenu.Show((IntPtr)1, new[] { @"C:\one\a.txt", @"C:\two\b.txt" }, native));
        Assert.Equal(0, native.MenusShown);
        Assert.Empty(native.Paths);
    }

    [Fact]
    public void OneUnresolvedItemRefusesTheWholeSelection()
    {
        using var native = new RecordingSession { RejectPath = @"C:\one\missing.txt" };
        Assert.False(ShellContextMenu.Show((IntPtr)1, new[] { @"C:\one\a.txt", @"C:\one\missing.txt" }, native));
        Assert.Equal(0, native.MenusShown);
    }

    [Fact]
    public void DriveRootHasANamespaceParentAndCanReachItsMenu()
    {
        string root = System.IO.Path.GetPathRoot(Environment.SystemDirectory)!;
        using var native = ShellContextMenu.CreateSession();
        Assert.True(native.TryInitialize(root));
        Assert.True(native.TryAddPath(root));
        using var display = new RecordingSession();
        Assert.True(ShellContextMenu.Show((IntPtr)1, new[] { root }, display));
        Assert.Equal(new[] { root }, display.Paths);
        Assert.Equal(1, display.MenusShown);
    }

    private sealed class RecordingSession : ShellContextMenu.IShellMenuSession
    {
        public readonly List<string> Paths = new();
        public int MenusShown;
        public string? RejectPath;
        public bool TryInitialize(string firstPath) => true;
        public bool TryAddPath(string path)
        {
            if (path == RejectPath) return false;
            Paths.Add(path);
            return true;
        }
        public bool Show(IntPtr owner) { MenusShown++; return true; }
        public void Dispose() { }
    }
}
