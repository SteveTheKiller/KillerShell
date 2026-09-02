using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Shell
{
    public sealed class TerminalLaunchTests
    {
        [Theory]
        [InlineData(false, false, false)]
        [InlineData(false, true, false)]
        [InlineData(true, true, false)]
        [InlineData(true, false, true)]
        public void ElevatedRelaunch_IsOnlyNeededFromARegularWindow(
            bool profileElevated, bool windowElevated, bool expected)
            => Assert.Equal(expected,
                MainWindow.NeedsElevatedRelaunch(profileElevated, windowElevated));
    }
}
