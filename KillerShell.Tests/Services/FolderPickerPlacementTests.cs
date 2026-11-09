using KillerShell.Controls;
using Xunit;

namespace KillerShell.Tests.Services;

// The folder picker comes back where it was last closed, including on a monitor to the left of
// or above the primary one, where every coordinate is negative. A spot that is now off every
// screen is pulled back by the work-area fit when the window opens, not thrown away here.
public sealed class FolderPickerPlacementTests
{
    [Theory]
    [InlineData(-1500, 100)]
    [InlineData(200, -900)]
    [InlineData(100, 100)]
    public void ASavedPositionOnAnyMonitorIsKept(double x, double y)
        => Assert.True(FolderPickerDialog.UsableSavedPosition(x, y));

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.PositiveInfinity)]
    public void ANonsensePositionIsIgnored(double x, double y)
        => Assert.False(FolderPickerDialog.UsableSavedPosition(x, y));
}
