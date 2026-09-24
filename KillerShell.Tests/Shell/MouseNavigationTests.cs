using System;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Shell
{
    public sealed class MouseNavigationTests
    {
        [Theory]
        [InlineData(0x020C, 1L << 16, 0, -1)]
        [InlineData(0x020C, 2L << 16, 0, 1)]
        [InlineData(0x0319, 0, 1L << 16, -1)]
        [InlineData(0x0319, 0, 2L << 16, 1)]
        [InlineData(0x020B, 1L << 16, 0, 0)]
        [InlineData(0x0319, 0, 9L << 16, 0)]
        public void NativeMouseMessage_MapsOnlyBrowserHistoryCommands(
            int message, long wParam, long lParam, int expected)
            => Assert.Equal(expected, MainWindow.MouseHistoryDirectionFromMessage(
                message, new IntPtr(wParam), new IntPtr(lParam)));
    }
}
