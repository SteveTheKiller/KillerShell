using System;
using System.Reflection;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Shapes;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Shell;

public sealed class LayoutMenuIconTests
{
    // The tab placement and details pane rows draw their own outline icon. In the 16 px menu gutter
    // anything wider than the 12 px font glyphs touches the label, so it stays at glyph size.
    [Fact]
    public void LayoutRowIconsAreGlyphSized()
    {
        double width = 0, height = 0;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var build = typeof(MainWindow).GetMethod("LayoutMenuItem", BindingFlags.NonPublic | BindingFlags.Static)!;
                var item = (MenuItem)build.Invoke(null, new object[] { "Str_Layout_TabsBottom", "Alt+Shift+Down",
                    "M0,0 H14 V14 H0 Z M0,10 H14", (Action)(() => { }) })!;
                var icon = (Path)item.Icon;
                width = icon.Width + icon.StrokeThickness;
                height = icon.Height + icon.StrokeThickness;
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
        Assert.InRange(width, 1, 13);
        Assert.InRange(height, 1, 13);
    }
}
