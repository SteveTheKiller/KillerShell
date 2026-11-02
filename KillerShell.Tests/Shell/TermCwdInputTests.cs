using System;
using System.IO;
using KillerShell.Shell;
using KillerShell.Terminal;
using Xunit;

namespace KillerShell.Tests.Shell
{
    public sealed class TermCwdInputTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", "termcwd", Guid.NewGuid().ToString("N"));

        public TermCwdInputTests()
        {
            Directory.CreateDirectory(Path.Combine(_root, "sub"));
            File.WriteAllText(Path.Combine(_root, "sub", "tool.ps1"), "x");
            File.WriteAllText(Path.Combine(_root, "note.txt"), "x");
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

        [Fact]
        public void ARelativeFolderIsResolvedAgainstTheTabsFolder()
        {
            var target = TermCwdInput.Resolve("sub", TerminalProfile.PowerShell(), _root);
            Assert.Equal(TermCwdKind.Folder, target.Kind);
            Assert.Equal(Path.Combine(_root, "sub"), target.Path);
        }

        [Fact]
        public void ARelativeFileIsResolvedAgainstTheTabsFolder()
        {
            var target = TermCwdInput.Resolve(@"sub\tool.ps1", TerminalProfile.PowerShell(), _root);
            Assert.Equal(TermCwdKind.File, target.Kind);
            Assert.Equal(Path.Combine(_root, "sub", "tool.ps1"), target.Path);
        }

        [Fact]
        public void ADotDotStepIsResolvedToo()
        {
            var target = TermCwdInput.Resolve("..", TerminalProfile.PowerShell(), Path.Combine(_root, "sub"));
            Assert.Equal(TermCwdKind.Folder, target.Kind);
            Assert.Equal(_root, target.Path);
        }

        [Fact]
        public void AWslTabKeepsTheTextForTheDistribution()
        {
            var target = TermCwdInput.Resolve("projects", TerminalProfile.Wsl("Ubuntu"), "/home/steve");
            Assert.Equal(TermCwdKind.Folder, target.Kind);
            Assert.Equal("projects", target.Path);
        }

        [Fact]
        public void AMissingFolderIsNotFound()
        {
            Assert.Equal(TermCwdKind.NotFound, TermCwdInput.Resolve("nope", TerminalProfile.PowerShell(), _root).Kind);
        }

        [Theory]
        [InlineData("bad:name")]
        [InlineData("bad|name")]
        [InlineData("a<b")]
        public void TextWindowsCannotParseIsNotFoundInsteadOfThrowing(string typed)
        {
            Assert.Equal(TermCwdKind.NotFound, TermCwdInput.Resolve(typed, TerminalProfile.PowerShell(), _root).Kind);
        }

        [Fact]
        public void APathPastTheLengthLimitIsNotFoundInsteadOfThrowing()
        {
            string typed = new string('a', 40000);
            Assert.Equal(TermCwdKind.NotFound, TermCwdInput.Resolve(typed, TerminalProfile.PowerShell(), _root).Kind);
        }

        [Theory]
        [InlineData("a\rb.txt")]
        [InlineData("a\nb.txt")]
        [InlineData("a\0b.txt")]
        public void AFileWhoseNameCarriesALineBreakIsNeverSent(string name)
        {
            var target = TermCwdInput.Resolve(name, TerminalProfile.PowerShell(), _root, _ => true, _ => false);
            Assert.True(target.Kind is TermCwdKind.Ignore or TermCwdKind.NotFound, "Sent as " + target.Kind);
        }

        [Fact]
        public void AFolderWhoseNameCarriesALineBreakIsNeverSent()
        {
            var target = TermCwdInput.Resolve("a\rb", TerminalProfile.PowerShell(), _root, _ => false, _ => true);
            Assert.True(target.Kind is TermCwdKind.Ignore or TermCwdKind.NotFound, "Sent as " + target.Kind);
        }
    }
}
