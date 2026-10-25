using System;
using System.IO;
using System.Text;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class AddressTargetTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "KillerShell.Tests", Guid.NewGuid().ToString("N"));

        public AddressTargetTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string Make(string name, byte[] bytes)
        {
            string p = Path.Combine(_dir, name);
            File.WriteAllBytes(p, bytes);
            return p;
        }

        [Fact]
        public void AFolderIsBrowsed()
            => Assert.Equal(AddressTargetKind.Folder, AddressTarget.Classify(_dir));

        [Fact]
        public void AMarkdownFileOpensInTheEditor()
            => Assert.Equal(AddressTargetKind.Edit, AddressTarget.Classify(Make("review.md", Encoding.UTF8.GetBytes("# Review"))));

        [Fact]
        public void AnUnlistedExtensionHoldingPlainTextOpensInTheEditor()
            => Assert.Equal(AddressTargetKind.Edit, AddressTarget.Classify(Make("Program.cs", Encoding.UTF8.GetBytes("class P { }"))));

        [Fact]
        public void AnEmptyFileOpensInTheEditor()
            => Assert.Equal(AddressTargetKind.Edit, AddressTarget.Classify(Make("notes", [])));

        [Theory]
        [InlineData("tool.exe")]
        [InlineData("build.cmd")]
        [InlineData("build.bat")]
        [InlineData("deploy.ps1")]
        public void ProgramsAndScriptsGoToATerminal(string name)
            => Assert.Equal(AddressTargetKind.Terminal, AddressTarget.Classify(Make(name, Encoding.ASCII.GetBytes("echo hi"))));

        [Fact]
        public void BinaryDocumentsOpenThroughTheirAssociation()
            => Assert.Equal(AddressTargetKind.Associated, AddressTarget.Classify(Make("scan.pdf", [0x25, 0x50, 0x44, 0x46, 0x00, 0x01])));

        [Fact]
        public void AMissingPathIsMissing()
            => Assert.Equal(AddressTargetKind.Missing, AddressTarget.Classify(Path.Combine(_dir, "nope.md")));

        [Theory]
        [InlineData(true, @"& '.\deploy.ps1'", "deploy.ps1")]
        [InlineData(true, @"& '.\it''s.exe'", "it's.exe")]
        [InlineData(false, "\"build.cmd\"", "build.cmd")]
        public void TheStagedCommandRunsTheFileFromItsOwnFolder(bool powerShell, string expected, string name)
            => Assert.Equal(expected, AddressTarget.StagedCommand(
                powerShell ? KillerShell.Terminal.TerminalShellKind.PowerShell : KillerShell.Terminal.TerminalShellKind.Cmd, name));

        [Fact]
        public void WslGetsNoStagedCommand()
            => Assert.Null(AddressTarget.StagedCommand(KillerShell.Terminal.TerminalShellKind.Wsl, "tool.exe"));
    }
}
