using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        private const string DetailsBottomIcon = "M0,0 H14 V14 H0 Z M0,10 H14";
        private const string DetailsRightIcon = "M0,0 H14 V14 H0 Z M10,0 V14";
        private const string DetailsOffIcon = "M0,0 H14 V14 H0 Z M2,2 L12,12";

        private void PopulateDetailsPaneMenu(FilePane pane)
        {
            // A small strip card, rebuilt on each opening so the layout in use is the lit tile.
            var menu = Controls.CardMenus.Prepare(pane.DetailsPaneBtn.ContextMenu);
            Controls.CardMenus.WatchKeyboardStart(menu);
            menu.Items.Add(Controls.CardMenus.Tiles(menu, new[]
            {
                Controls.CardMenus.Tile("detailsbottom", 0xE74B, "Str_Layout_DetailsBottom", null, "Ctrl+Alt+Down",
                    () => SetDetailsPanePosition(pane, 1), on: pane.DetailsPaneOpen && !pane.DetailsPaneOnRight),
                Controls.CardMenus.Tile("detailsright", 0xE72A, "Str_Layout_DetailsRight", null, "Ctrl+Alt+Right",
                    () => SetDetailsPanePosition(pane, 2), on: pane.DetailsPaneOpen && pane.DetailsPaneOnRight),
                Controls.CardMenus.Tile("detailsoff", 0xE711, "Str_Layout_DetailsOff", null, "Ctrl+Alt+0",
                    () => SetDetailsPanePosition(pane, 0), on: !pane.DetailsPaneOpen),
            }));
        }

        private static void UpdateDetailsPaneButton(FilePane pane)
        {
            pane.DetailsPaneGlyph.Data = Geometry.Parse(!pane.DetailsPaneOpen ? DetailsOffIcon
                : pane.DetailsPaneOnRight ? DetailsRightIcon : DetailsBottomIcon);
            pane.DetailsPaneBtn.SetResourceReference(FrameworkElement.ToolTipProperty,
                !pane.DetailsPaneOpen ? "Str_TT_DetailsBottom" : pane.DetailsPaneOnRight ? "Str_TT_DetailsOff" : "Str_TT_DetailsRight");
        }

        private void SetPaneOrientation(bool sideBySide)
        {
            if (DualPane && _paneSideBySide == sideBySide) return;
            FinishPendingPaneSlide();
            // Stacking a split that F10 opened by growing the window: the extra width was for
            // the side pane, so it goes back now. Otherwise the stacked close cannot return it
            // and every F10-then-cycle leaves the window wider.
            double giveBack = DualPane && _paneSideBySide && !sideBySide && _paneOpenGrewWindow
                && WindowState == WindowState.Normal ? PaneColB.ActualWidth + SplitPx : 0;
            _paneSideBySide = sideBySide;
            if (!DualPane) OpenSecondPane();
            else ApplyPaneLayout();
            if (giveBack > 0)
            {
                _paneOpenGrewWindow = false;
                BeginAnimation(WidthProperty, null);
                Width = System.Math.Max(MinWidth, Width - giveBack);
                double slideBack = _paneShiftLeft;
                _paneShiftLeft = 0;
                if (slideBack > 0)
                    Dispatcher.BeginInvoke(new Action(() =>
                        ShiftWindowDip(System.Math.Min(slideBack, MonitorRoomToGrowRightDip()))),
                        System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }
    }
}
