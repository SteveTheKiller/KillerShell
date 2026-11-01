using System;
using System.IO;
using KillerFamily.CommandLine;
using Xunit;

namespace KillerShell.Tests;

// The current folder is process-wide, so these tests never run beside each other or beside
// anything that reads it.
[Collection("CurrentFolder")]
public sealed class CommandLineEntryTests
{
    private static readonly string[] Commands = ["list", "read", "drives"];
    private static readonly string[] NeedsArgument = ["list", "read"];

    private static string[]? Route(string folder, params string[] arguments)
    {
        string before = Environment.CurrentDirectory;
        Environment.CurrentDirectory = folder;
        try { return CommandLineEntry.GetArguments(arguments, Commands, argumentCommands: NeedsArgument); }
        finally { Environment.CurrentDirectory = before; }
    }

    private static string NewFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "ks-entry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void ABareVerbNamingAnExistingFileOpensTheUi()
    {
        string folder = NewFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, "list"), "a file that happens to be called list");
            Assert.Null(Route(folder, "list"));
            Assert.Null(Route(folder, "LIST"));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void ABareVerbNamingAnExistingFolderOpensTheUi()
    {
        string folder = NewFolder();
        try
        {
            Directory.CreateDirectory(Path.Combine(folder, "read"));
            Assert.Null(Route(folder, "read"));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void AVerbWithNothingOnDiskStillRoutesToTheCommand()
    {
        string folder = NewFolder();
        try
        {
            Assert.Equal(["list"], Route(folder, "list"));
            Assert.Equal(["list", "C:\\data"], Route(folder, "list", "C:\\data"));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void AFileNamedLikeAVerbDoesNotStopAVerbThatHasItsArgument()
    {
        string folder = NewFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, "list"), "x");
            Assert.Equal(["list", "C:\\data"], Route(folder, "list", "C:\\data"));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void AVerbThatNeedsNoArgumentIgnoresFilesOnDisk()
    {
        string folder = NewFolder();
        try
        {
            File.WriteAllText(Path.Combine(folder, "drives"), "x");
            Assert.Equal(["drives"], Route(folder, "drives"));
        }
        finally { Directory.Delete(folder, true); }
    }
}
