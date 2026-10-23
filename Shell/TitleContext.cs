using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        private TextBlock? _activeTitleText;

        private void UpdateTitleContext()
        {
            if (TitleContextHost == null || Pane.Active == null) return;
            if (_activeTitleText == null)
            {
                _activeTitleText = new TextBlock
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    IsHitTestVisible = false
                };
                _activeTitleText.SetResourceReference(TextBlock.FontFamilyProperty, "FileNameFont");
                _activeTitleText.SetResourceReference(TextBlock.FontSizeProperty, "FileNameFontSize");
                _activeTitleText.SetResourceReference(TextBlock.ForegroundProperty, "ChromeTextBrush");
                TitleContextHost.Content = _activeTitleText;
            }
            _activeTitleText.SetBinding(TextBlock.TextProperty, new Binding("Title") { Source = Pane.Active });
        }
    }
}
