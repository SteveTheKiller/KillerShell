using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace KillerShell.Shell
{
    /// <summary>
    /// Where every rail flyout opens: the BOTTOM-LEFT CORNER OF THE CONTENT PANE.
    /// (From KillerPDF's Controls/FlyoutPlacement.cs - the family flyout standard, copied
    /// verbatim, Steve 2026-08-02: "COPY THE EXACT MENU FROM KILLERPDF FOR EVERYTHING.")
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
    /// Button.ContextMenu exactly like LangMenu (Steve, 2026-08-02) - so both flyouts now go
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
            menu.CustomPopupPlacementCallback =
                (popupSize, targetSize, __) => BottomLeftOfPane(popupSize, targetSize);
        }

        /// <summary>
        /// Coordinates are relative to the placement target's top-left - the pane's top-left. So
        /// x = 0 is the pane's left edge (clear of the rail) and y = pane height - flyout height
        /// puts the flyout's bottom on the pane's bottom (clear of the footer).
        /// </summary>
        private static CustomPopupPlacement[] BottomLeftOfPane(Size popupSize, Size targetSize)
        {
            double y = targetSize.Height - popupSize.Height;

            // A flyout taller than the pane would otherwise start above it and run over the
            // toolbar; pin it to the pane's top instead and let it use the height it has.
            if (y < 0) y = 0;

            return new[] { new CustomPopupPlacement(new Point(0, y), PopupPrimaryAxis.None) };
        }
    }
}
