using System.Globalization;
using System.Threading;
using System.Windows;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services;

// The size and place a new window opens at: the last window's, snapped size included.
public sealed class SavedWindowBoundsTests
{
    [Fact]
    public void ASnappedHalfScreenWindowComesBackAtThatSize()
    {
        string saved = SavedWindowBounds.Format(new Rect(0, 0, 640, 690), maximized: false);

        Assert.True(SavedWindowBounds.TryParse(saved, out Rect bounds, out bool maximized));
        Assert.Equal(new Rect(0, 0, 640, 690), bounds);
        Assert.False(maximized);
    }

    [Fact]
    public void AMaximizedWindowRemembersItsOrdinarySizeToo()
    {
        string saved = SavedWindowBounds.Format(new Rect(100, 50, 900, 600), maximized: true);

        Assert.True(SavedWindowBounds.TryParse(saved, out Rect bounds, out bool maximized));
        Assert.Equal(new Rect(100, 50, 900, 600), bounds);
        Assert.True(maximized);
    }

    [Fact]
    public void ALanguageWithCommaDecimalsReadsTheSameValue()
    {
        var before = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            string saved = SavedWindowBounds.Format(new Rect(10.5, 20.25, 800.5, 600.75), maximized: false);
            Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");

            Assert.True(SavedWindowBounds.TryParse(saved, out Rect bounds, out _));
            Assert.Equal(new Rect(10.5, 20.25, 800.5, 600.75), bounds);
        }
        finally { Thread.CurrentThread.CurrentCulture = before; }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("1,2,3")]
    [InlineData("0,0,0,600,0")]
    [InlineData("0,0,800,-5,0")]
    public void AnythingElseOpensAtTheDefaultSize(string? saved)
        => Assert.False(SavedWindowBounds.TryParse(saved, out _, out _));
}
