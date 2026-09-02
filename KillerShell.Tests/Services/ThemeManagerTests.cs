using System.Windows.Media;
using KillerShell.Services;
using Xunit;

namespace KillerShell.Tests.Services
{
    public sealed class ThemeManagerTests
    {
        [Fact]
        public void LightTitleBarColor_UsesRightmostGradientStop()
        {
            var gradient = new LinearGradientBrush();
            gradient.GradientStops.Add(new GradientStop(Color.FromRgb(0x00, 0x00, 0x80), 0));
            gradient.GradientStops.Add(new GradientStop(Color.FromRgb(0x10, 0x84, 0xD0), 1));

            Assert.Equal(Color.FromRgb(0x10, 0x84, 0xD0),
                ThemeManager.LightTitleBarColor(gradient, Colors.Red));
        }

        [Fact]
        public void LightTitleBarColor_UsesFallbackForSolidTitleBar()
            => Assert.Equal(Colors.Teal,
                ThemeManager.LightTitleBarColor(Brushes.Black, Colors.Teal));
    }
}
