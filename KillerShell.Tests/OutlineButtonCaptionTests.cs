using System;
using System.Windows;
using System.Windows.Controls;
using Xunit;

namespace KillerShell.Tests
{
    /// <summary>
    /// A dialog's confirm button (Unlock) keeps hinted text: an effect on its caption would
    /// rasterize it and blur it at rest. Loads the shipped Black theme and kit styles.
    /// </summary>
    public sealed class OutlineButtonCaptionTests
    {
        [Fact]
        public void OutlineButtonCaptionsAreNotRasterized()
        {
            Exception? failure = null;
            var thread = new System.Threading.Thread(() =>
            {
                try { Check(); }
                catch (Exception ex) { failure = ex; }
                finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (failure != null) throw failure;
        }

        private static void Check()
        {
            var root = new Border { Width = 300 };
            foreach (string path in new[] { "Themes/Black.xaml", "Controls/Controls.xaml" })
                root.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri("pack://application:,,,/KillerShell;component/" + path, UriKind.Absolute),
                });
            var button = new Button { Content = "Unlock", Style = (Style)root.FindResource("OutlineButton") };
            root.Child = button;
            root.Measure(new Size(300, double.PositiveInfinity));
            root.Arrange(new Rect(root.DesiredSize));
            root.UpdateLayout();

            var label = (ContentPresenter)button.Template.FindName("label", button);
            Assert.NotNull(label);
            Assert.Null(label.Effect);
        }
    }
}
