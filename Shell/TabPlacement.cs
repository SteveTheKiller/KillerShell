using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerShell.Services;

namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        private static readonly object TabPlacementMenuTag = new object();
        private static readonly Transform BottomTabTransform = (Transform)new ScaleTransform(1, -1).GetAsFrozen();
        private static readonly string[] TabPlacementMargins =
        {
            "TabMargin", "TabActiveMargin", "TabInactiveFirstMargin", "TabInactiveLastMargin",
            "TabActiveFirstMargin", "TabActiveLastMargin", "TabActiveOnlyMargin"
        };

        internal bool TabsAtBottom { get; private set; }

        private void InitTabPlacement()
            => SetTabPlacement(GetWindowViewSetting("TabsAtBottom") == "1", persist: false);

        private void SetTabPlacement(bool bottom, bool persist = true)
        {
            TabsAtBottom = bottom;
            Grid.SetRow(LeftPane.TabBar, bottom ? 2 : 0);
            Grid.SetRow(RightPane.TabBar, bottom ? 2 : 0);
            if (persist) SetWindowViewSetting("TabsAtBottom", bottom ? "1" : "0");
            UpdateTabBar();
            RefreshActiveTabSurface(LeftPane);
            RefreshActiveTabSurface(RightPane);
        }

        internal void TabPlacementMenu_Opened(object sender, RoutedEventArgs e)
        {
            var menu = (ContextMenu)sender;
            for (int index = menu.Items.Count - 1; index >= 0; index--)
                if (menu.Items[index] is FrameworkElement item && ReferenceEquals(item.Tag, TabPlacementMenuTag))
                    menu.Items.RemoveAt(index);
            if (menu.Items.Count > 0) menu.Items.Add(new Separator { Tag = TabPlacementMenuTag });

            void AddChoice(bool bottom)
            {
                var item = LayoutMenuItem(bottom ? "Str_Layout_TabsBottom" : "Str_Layout_TabsTop",
                    bottom ? "Alt+Shift+Down" : "Alt+Shift+Up",
                    bottom ? "M0,0 H14 V14 H0 Z M0,10 H14" : "M0,0 H14 V14 H0 Z M0,4 H14",
                    () => SetTabPlacement(bottom));
                item.Tag = TabPlacementMenuTag;
                item.IsCheckable = true;
                item.IsChecked = TabsAtBottom == bottom;
                menu.Items.Add(item);
            }
            AddChoice(false);
            AddChoice(true);
        }

        private void ApplyTabPlacementChrome(FilePane pane)
        {
            bool bottom = TabsAtBottom && pane.TabBar.Visibility == Visibility.Visible;
            Transform tabTransform = bottom ? BottomTabTransform : Transform.Identity;
            if (!ReferenceEquals(pane.TabBar.Resources["TabPlacementTransform"], tabTransform))
                pane.TabBar.Resources["TabPlacementTransform"] = tabTransform;
            // Only this pane's strip reverses its connection gap; shared theme values stay intact.
            foreach (string key in TabPlacementMargins)
            {
                if (bottom && Application.Current.TryFindResource(key) is Thickness margin)
                {
                    var reversed = ReverseVertical(margin);
                    if (!Equals(pane.TabBar.Resources[key], reversed)) pane.TabBar.Resources[key] = reversed;
                }
                else pane.TabBar.Resources.Remove(key);
            }
            // The flip already puts each tab's white highlight on its foot. The edge the tabs hang
            // from is the pane's bottom, which Win98 shades gray, not the white top edge a top
            // strip sits on - so the line beside the active tab turns gray down here.
            string joinBrush = bottom ? "BevelDarkBrush" : "BevelLightBrush";
            pane.RetroTabInnerJoinLeft.SetResourceReference(Border.BackgroundProperty, joinBrush);
            pane.RetroTabInnerJoinRight.SetResourceReference(Border.BackgroundProperty, joinBrush);
            // The active tab shares the pane's face, so its flipped white foot vanishes into it;
            // a gray foot (template "top", flipped) closes it off. 98SE only.
            // The dividers (each tab's gray and black right edge) stop one row short of the white
            // foot, as a Win98 tab's dark edge stops short of its corner. Template "top" is the
            // visual foot here.
            if (bottom && ThemeManager.Current == Theme.SE98)
            {
                pane.TabBar.Resources["TabActiveBevelDarkThickness"] = new Thickness(0, 1, 1, 0);
                foreach (var (key, source) in new[] { ("TabBevelDarkMargin", "TabBevelMargin"),
                                                      ("TabInactiveOuterOutlineMargin", "TabInactiveOuterOutlineMargin") })
                    if (Application.Current.TryFindResource(source) is Thickness m)
                        pane.TabBar.Resources[key] = new Thickness(m.Left, m.Top + 1, m.Right, m.Bottom);
            }
            else
            {
                pane.TabBar.Resources.Remove("TabActiveBevelDarkThickness");
                pane.TabBar.Resources.Remove("TabBevelDarkMargin");
                pane.TabBar.Resources.Remove("TabInactiveOuterOutlineMargin");
            }
            pane.TabBandShadow.RenderTransform = tabTransform;
            pane.RetroTabJoinLine.VerticalAlignment = bottom ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            pane.RetroTabJoinLine.Margin = bottom ? new Thickness(0, 1, 0, 0) : new Thickness(0, 0, 0, 1);
            pane.RetroTabInnerJoin.VerticalAlignment = bottom ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            pane.RetroTabInnerJoin.Margin = bottom ? new Thickness(1, -1, 2, 0) : new Thickness(1, 0, 2, -1);

            foreach (var edge in new[] { pane.ResultsPaneBottomEdge, pane.PaneFocusBottomEdge })
            {
                edge.SetResourceReference(Border.BorderThicknessProperty, "PaneEdgeBottomThickness");
                if (bottom) edge.BorderThickness = ReverseVertical(edge.BorderThickness);
            }
            // The list surface rounds its foot only when it IS the pane's bottom edge; the same
            // rule UpdatePaneStatusBar applies, re-run here because placement changes it.
            double surfaceFoot = bottom || pane.PaneStatusBar.Visibility == Visibility.Visible ? 0 : PaneRadius;
            pane.ResultsSurface.CornerRadius = new CornerRadius(0, 0, surfaceFoot, surfaceFoot);
            pane.ResultsSurfaceGrain.CornerRadius = pane.ResultsSurface.CornerRadius;
            pane.PaneBevelOuterDark.SetResourceReference(Border.BorderBrushProperty, "DocumentPaneBevelTopLeftBrush");
            if (!bottom) return;

            pane.ResultsPane.CornerRadius = new CornerRadius(PaneRadius, PaneRadius, 0, 0);
            pane.ScopeBar.CornerRadius = new CornerRadius(BarRadius, 0, 0, 0);
            pane.ResultsPane.BorderThickness = ReverseVertical(pane.ResultsPane.BorderThickness);
            if (ThemeManager.Current == Theme.SE98)
            {
                pane.ResultsPane.Padding = ReverseVertical(pane.ResultsPane.Padding);
                foreach (var bevel in new[] { pane.PaneBevelOuterDark, pane.PaneBevelOuterLight, pane.PaneBevelInnerLight })
                {
                    bevel.Margin = ReverseVertical(bevel.Margin);
                    bevel.BorderThickness = ReverseVertical(bevel.BorderThickness);
                }
                // The page's left column is white under a top strip, continuing the first tab's
                // white edge down the pane; a bottom strip mirrors it, so it turns dark.
                pane.PaneBevelOuterDark.SetResourceReference(Border.BorderBrushProperty, "BevelDarkBrush");
            }
        }

        private static Thickness ReverseVertical(Thickness value)
            => new Thickness(value.Left, value.Bottom, value.Right, value.Top);
    }
}
