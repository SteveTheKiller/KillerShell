using System.Windows;
using System.Windows.Controls;
using KillerFamily.Accessibility;

namespace KillerShell.Shell
{
    // APP-OWNED: KillerShell's look for the kit's themed system menu (Shell/SystemMenu.cs). The
    // icons take the MenuIcon style like every other KillerShell menu, and the rows keep the
    // MenuItem style's own padding.
    public partial class MainWindow
    {
        partial void CustomizeSystemMenuItem(MenuItem item, Glyph icon, int command, bool danger)
        {
            item.ClearValue(Control.PaddingProperty);
            icon.ClearValue(TextBlock.FontFamilyProperty);
            icon.ClearValue(TextBlock.FontSizeProperty);
            icon.ClearValue(FrameworkElement.HorizontalAlignmentProperty);
            icon.ClearValue(FrameworkElement.VerticalAlignmentProperty);
            icon.SetResourceReference(FrameworkElement.StyleProperty, "MenuIcon");
            if (!danger) icon.ClearValue(TextBlock.ForegroundProperty);
        }
    }
}
