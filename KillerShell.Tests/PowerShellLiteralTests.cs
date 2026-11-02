using System.Linq;
using KillerShell.Services;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests;

// PowerShell treats the curly single quotes as quote characters, so a path holding one has to be
// doubled like an ASCII quote or it ends the literal early and the rest of the path runs as code.
public sealed class PowerShellLiteralTests
{
    [Theory]
    [InlineData("O'Neil")]
    [InlineData("O\u2018Neil")]
    [InlineData("O\u2019Neil")]
    [InlineData("O\u201AExample")]
    [InlineData("O\u201BExample")]
    [InlineData("a'\u2019;calc;'b")]
    public void ChangeDirectoryReadsBackTheExactPath(string name)
    {
        string path = @"C:\Users\" + name + @"\Folder";
        string command = TerminalProfile.PowerShell().ChangeDirectoryCommand(path);

        var parsed = PowerShellParse.Parse(command);

        Assert.Null(parsed.Error);
        Assert.Equal(path, parsed.Strings.Last());
        Assert.Equal(2, parsed.Strings.Count);
    }

    [Theory]
    [InlineData("O'Neil.exe")]
    [InlineData("O\u2018Neil.exe")]
    [InlineData("O\u2019Neil.exe")]
    [InlineData("O\u201AExample.exe")]
    [InlineData("O\u201BExample.exe")]
    public void StagedCommandReadsBackTheExactFileName(string name)
    {
        string command = AddressTarget.StagedCommand(TerminalShellKind.PowerShell, name)!;

        var parsed = PowerShellParse.Parse(command);

        Assert.Null(parsed.Error);
        Assert.Equal(@".\" + name, parsed.Strings.Single());
    }
}
