using System;
using System.Threading;
using System.Windows.Controls;
using KillerShell.Shell;
using Xunit;

namespace KillerShell.Tests.Shell
{
    public sealed class SearchFlyoutPressTests
    {
        private static void OnSta(Action work)
        {
            Exception? failure = null;
            var thread = new Thread(() => { try { work(); } catch (Exception error) { failure = error; } });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
            if (failure != null) throw failure;
        }

        [Fact]
        public void PressesInsideTheFlyoutSurfaceAndPatternHelpStayInsideAndOthersDoNot()
        {
            OnSta(() =>
            {
                var surface = new Grid();
                var field = new TextBox();
                var helpHost = new ContentControl();
                var helpText = new TextBlock();
                helpHost.Content = helpText;
                surface.Children.Add(field);
                surface.Children.Add(helpHost);
                var outside = new Button();

                Assert.True(MainWindow.IsPressInside(field, surface));
                Assert.True(MainWindow.IsPressInside(helpText, surface));
                Assert.True(MainWindow.IsPressInside(surface, surface));
                Assert.False(MainWindow.IsPressInside(outside, surface));
                Assert.False(MainWindow.IsPressInside(null, surface));
            });
        }
    }
}
