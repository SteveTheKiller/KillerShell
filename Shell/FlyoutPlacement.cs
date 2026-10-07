using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace KillerShell.Shell
{
    /// <summary>
    /// Where every rail flyout opens: the BOTTOM-LEFT CORNER OF THE CONTENT PANE.
    /// (From KillerPDF's Controls/FlyoutPlacement.cs - the family flyout standard, copied
    /// verbatim, 2026-08-02.)
    ///
    /// That corner is the answer because of what bounds it, and all three matter:
    ///   - it is INSIDE the window, so a flyout never hangs over the desktop;
    ///   - it is ABOVE the footer, so the status bar is never covered;
    ///   - it is clear of the icon rail, so the rail buttons are never covered.
    /// The content pane (PaneHost in MainWindow.xaml) is the one element bounded by all three at
    /// once, so flyouts are positioned against IT - not against the button, and not by any
    /// built-in placement mode.
    ///
    /// WHY NOT PlacementMode.Right / Top / etc: a Popup (and a ContextMenu, which is hosted in
    /// one) is its own top-level window, and WPF's built-in modes only ever avoid the SCREEN
    /// edge. They do not know the app window exists, let alone the footer or the rail. This is
    /// exactly what threw the flyout off into empty space when Placement="Right" was tried
    /// directly against the rail button.
    ///
    /// THE EARLIER BUG: ThemeFlyout used to be a raw Popup, wired through this same class, and
    /// still landed in the wrong spot no matter how this callback was tuned. LangMenu - always a
    /// Button.ContextMenu, never a Popup - opened correctly the whole time with the exact same
    /// Attach/BottomLeftOfPane code below. The Popup path was the difference, not the math.
    /// Rather than keep patching a Popup's placement timing, ThemeFlyout was rebuilt as a
    /// Button.ContextMenu exactly like LangMenu (2026-08-02) - so both flyouts now go
    /// through the identical, already-proven-correct code path. The Popup overload below stays
    /// only because KillerPDF's own FlyoutPlacement.cs keeps it for any future Popup-based
    /// caller; nothing in KillerShell uses it anymore.
    ///
    /// WIRING (each time a flyout opens):
    ///     FlyoutPlacement.UsePane(PaneHost);            // the element the results/tabs sit on
    ///     FlyoutPlacement.Attach(themeMenu, themeButton);
    ///     themeMenu.IsOpen = true;
    /// </summary>
    internal static class FlyoutPlacement
    {
        /// <summary>The content pane. Set before every attach; every flyout positions against it.</summary>
        private static FrameworkElement? _pane;

        internal static void UsePane(FrameworkElement pane) => _pane = pane;

        internal static void Attach(Popup popup, UIElement _)
        {
            popup.PlacementTarget = _pane;
            popup.Placement = PlacementMode.Custom;
            popup.CustomPopupPlacementCallback =
                (popupSize, targetSize, __) => BottomLeftOfPane(popupSize, targetSize);
        }

        internal static void Attach(ContextMenu menu, UIElement _)
        {
            menu.PlacementTarget = _pane;
            menu.Placement = PlacementMode.Custom;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 0;
            menu.CustomPopupPlacementCallback =
                (popupSize, targetSize, __) => BottomLeftOfPane(popupSize, targetSize);
        }

        /// <summary>
        /// Coordinates are relative to the pane. Compensate the card's shadow margin so its
        /// visible left and bottom edges stay eight pixels inside the pane.
        /// </summary>
        private static CustomPopupPlacement[] BottomLeftOfPane(Size popupSize, Size targetSize)
        {
            var halo = _pane?.TryFindResource("FlyoutCard") is Style style
                ? new Border { Style = style }.Margin : new Thickness(22, 18, 22, 26);
            const double inset = 8;
            double x = inset - halo.Left;
            double y = targetSize.Height - popupSize.Height + halo.Bottom - inset;
            if (y < 0) y = 0;
            return new[] { new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.None) };
        }
    }
}
