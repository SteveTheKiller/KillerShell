using KillerShell.Models;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class ProcessListLogicTests
    {
        [Fact]
        public void MatchesProcess_SearchesNamePathAndUser()
        {
            var process = new ProcessInfo(42)
            {
                Name = "pwsh",
                Path = @"C:\Program Files\PowerShell\pwsh.exe",
                User = "DOMAIN\\steve",
            };

            Assert.True(ProcessListLogic.Matches(process, "POWER"));
            Assert.True(ProcessListLogic.Matches(process, "steve"));
            Assert.False(ProcessListLogic.Matches(process, "notepad"));
        }

        [Theory]
        [InlineData("\"C:\\Program Files\\Tool\\tool.exe\" --demo", "C:\\Program Files\\Tool\\tool.exe")]
        [InlineData("C:\\Tools\\tool.exe --demo", "C:\\Tools\\tool.exe")]
        public void ExtractExePath_ReturnsLeadingExecutable(string commandLine, string expected)
            => Assert.Equal(expected, ProcessListLogic.ExtractExePath(commandLine));

        [Theory]
        [InlineData("\"C:\\Program Files\\Tool\\tool.exe\" --demo -v", "--demo -v")]
        [InlineData("tool.exe --demo", "--demo")]
        [InlineData("tool.exe", "")]
        public void ExtractArguments_RemovesLeadingExecutable(string commandLine, string expected)
            => Assert.Equal(expected, ProcessListLogic.ExtractArguments(commandLine));

        [Fact]
        public void PinnedProcess_StaysBoundToTheOriginalAfterItExits()
        {
            using var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "ping.exe", "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;

            using var pinned = ProcessListLogic.PinProcess(child.Id);
            Assert.NotNull(pinned);

            child.Kill();
            child.WaitForExit();

            // The pinned handle still names the exited process, so a later kill cannot land on
            // whatever new process was handed the same id.
            Assert.True(pinned!.HasExited);
        }

        [Fact]
        public void PinProcess_ReturnsNullForAMissingId()
            => Assert.Null(ProcessListLogic.PinProcess(int.MaxValue - 3));
    }
}
