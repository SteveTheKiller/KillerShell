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

        // A dotfile such as .env has no extension Windows knows, so the text sniff is what sends
        // it to an editor tab instead of to an association that asks which app to use.
        [Fact]
        public void ADotEnvFileOpensInTheEditor()
            => Assert.Equal(AddressTargetKind.Edit, AddressTarget.Classify(Make(".env", Encoding.UTF8.GetBytes("API_KEY=x\nDEBUG=1\n"))));

        // A relative address is relative to the folder the tab is showing, the way Explorer's
        // address bar reads it, never to KillerShell's own working directory.
        [Theory]
        [InlineData("..", @"C:\Users\x\Documents", @"C:\Users\x")]
        [InlineData("Downloads", @"C:\Users\x", @"C:\Users\x\Downloads")]
        [InlineData(@"..\Music", @"C:\Users\x\Documents", @"C:\Users\x\Music")]
        [InlineData(@".\sub", @"\\nas\share\top", @"\\nas\share\top\sub")]
        [InlineData("D:", @"C:\Users\x", @"D:\")]
        [InlineData(@"D:work", @"C:\Users\x", @"D:\work")]
        [InlineData(@"\Windows", @"D:\games\deep", @"D:\Windows")]
        [InlineData(@"E:\abs\path", @"C:\Users\x", @"E:\abs\path")]
        [InlineData(@"\\server\share", @"C:\Users\x", @"\\server\share")]
        public void ATypedAddressResolvesAgainstTheTabFolder(string typed, string tabFolder, string expected)
            => Assert.Equal(expected, AddressTarget.ResolveTyped(typed, tabFolder));

        [Fact]
        public void AnEnvironmentVariableExpandsFirst()
            => Assert.Equal(Path.GetFullPath(Environment.GetEnvironmentVariable("TEMP")!),
                AddressTarget.ResolveTyped("%TEMP%", @"C:\Users\x"));

        [Theory]
        [InlineData(":ThisPC:", @"C:\Users\x")]
        public void APlaceOrAnAddressWithNoFolderBehindItIsLeftAlone(string typed, string tabFolder)
            => Assert.Equal(typed, AddressTarget.ResolveTyped(typed, tabFolder));

        [Theory]
        [InlineData("..", @"C:\work\src.zip?docs/sub", @"C:\work\src.zip?docs")]
        [InlineData(@"..\images", @"C:\work\src.zip?docs/sub", @"C:\work\src.zip?docs/images")]
        [InlineData("./readme", @"C:\work\src.zip?docs", @"C:\work\src.zip?docs/readme")]
        [InlineData("../..", @"C:\work\src.zip?docs/sub", @"C:\work\src.zip")]
        [InlineData("../..", @"C:\work\src.zip?docs", @"C:\work")]
        [InlineData("..", @"C:\work\src.zip", @"C:\work")]
        [InlineData("../sibling", @"C:\work\src.zip", @"C:\work\sibling")]
        [InlineData("..", ":ThisPC:", null)]
        [InlineData("Documents", ":ThisPC:", null)]
        [InlineData(@"\Windows", ":ThisPC:", null)]
        public void ARelativeAddressUsesTheTabsVirtualLocationOrIsRefused(string typed, string tabFolder, string? expected)
            => Assert.Equal(expected, AddressTarget.ResolveTyped(typed, tabFolder));

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
        public void ATextFileOverTheEditorLimitOpensThroughItsAssociation()
        {
            string big = Path.Combine(_dir, "huge.md");
            using (var stream = new FileStream(big, FileMode.Create)) stream.SetLength(32L * 1024 * 1024 + 1);
            Assert.Equal(AddressTargetKind.Associated, AddressTarget.Classify(big));
        }

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
