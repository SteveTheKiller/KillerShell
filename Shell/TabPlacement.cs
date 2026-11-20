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
        {
            // The details rule's break follows the active tab (Tabs.cs UpdateDetailsTabJoin), so it
            // is redrawn whenever the panel, the strip or the search summary changes size or shows.
            foreach (var pane in new[] { LeftPane, RightPane })
            {
                var p = pane;
                p.DetailsPaneBottomEdge.SizeChanged += (_, _) => UpdateDetailsTabJoin(p);
                p.TabStrip.SizeChanged += (_, _) => UpdateDetailsTabJoin(p);
                p.PaneStatusBar.IsVisibleChanged += (_, _) => UpdateDetailsTabJoin(p);
                p.TabBar.IsVisibleChanged += (_, _) => UpdateDetailsTabJoin(p);
            }
            SetTabPlacement(GetWindowViewSetting("TabsAtBottom") == "1", persist: false);
        }

        internal void SetTabPlacement(bool bottom, bool persist = true)
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
            // Both cards are rebuilt on every opening: the tab card is about the tab it opened on, and
            // the strip's own card shows where the strip is now. Both are small, so this stays quick.
            var menu = Controls.CardMenus.Prepare((ContextMenu)sender);
            var placement = new[]
            {
                Controls.CardMenus.Tile("tabstop", 0xE74A, "Str_Layout_TabsTop", null, "Alt+Shift+Up", () => SetTabPlacement(false), on: !TabsAtBottom),
                Controls.CardMenus.Tile("tabsbottom", 0xE74B, "Str_Layout_TabsBottom", null, "Alt+Shift+Down", () => SetTabPlacement(true), on: TabsAtBottom),
            };
            if (Equals(menu.Tag, "tabcard") && (menu.PlacementTarget as FrameworkElement)?.DataContext is Models.SearchTab tab
                && FillTabCard(menu, tab) is { } verbs)
            {
                menu.Items.Add(Controls.CardMenus.Tiles(menu, verbs, placement));
                return;
            }
            Controls.CardMenus.WatchKeyboardStart(menu);
            menu.Items.Add(Controls.CardMenus.Tiles(menu, placement));
        }

        /// <summary>
        /// The tab card: a header naming the tab and where it is, then tiles for New tab, the two moves,
        /// Search within these results and Close; the placement tiles follow (TabPlacementMenu_Opened).
        /// Every command acts on THIS tab and its pane, which need not be the focused ones. A tile that
        /// does not apply here stays in its place, dimmed.
        /// </summary>
        private KillerFamily.Controls.CardTile[]? FillTabCard(ContextMenu card, Models.SearchTab tab)
        {
            var pane = LeftPane.Tabs.Contains(tab) ? LeftPane : RightPane.Tabs.Contains(tab) ? RightPane : null;
            if (pane == null) return null;
            var other = ReferenceEquals(pane, LeftPane) ? RightPane : LeftPane;

            var head = Controls.CardMenus.Slot();
            head.Tag = "cardhead";
            Controls.CardMenus.SetHeader(head, OverflowRowIcon(tab, 32), tab.Title, TabCardMeta(tab));
            card.Items.Add(head);
            Controls.CardMenus.WatchKeyboardStart(card);

            // Piping takes the rows the tab lists, so a shell, document or tool tab has none.
            bool lists = tab.Term == null && tab.Editor == null && tab.Procs == null && tab.Events == null
                && tab.Registry == null && tab.Perf == null && tab.Storage == null;
            return
            [
                Controls.CardMenus.Tile("newtab", 0xE710, "Str_Card_NewTab", null, "Ctrl+T", () =>
                {
                    FocusPane(pane);
                    NewTab_Click(this, new RoutedEventArgs());   // Tabs.cs
                }),
                // The same move a drag across makes (PaneDrag.cs), appended to the other pane's strip.
                Controls.CardMenus.Tile("otherpane", 0xE8AB, "Str_Card_MoveOtherPane", "Str_Card_OtherPane", null, () =>
                {
                    if (!DualPane || !pane.Tabs.Contains(tab)) return;
                    FocusPane(pane);
                    MoveTabToPaneAt(tab, other, other.Tabs.Count);
                }, DualPane),
                Controls.CardMenus.Tile("newwindow", 0xE8A7, "Str_Card_MoveNewWindow", "Str_Card_NewWindow", null, () =>
                {
                    FocusPane(pane);
                    TearOutTab(tab);   // TabTearOut.cs, the same as dragging it out of the window
                }),
                Controls.CardMenus.Tile("pipe", 0xE721, "Str_Menu_PipeTab", null, "Ctrl+Shift+F", () =>
                {
                    FocusPane(pane);
                    PipeIntoNewTab(tab);   // Results.cs
                }, lists),
                Controls.CardMenus.Tile("close", 0xE8BB, "Str_Card_Close", null, "Ctrl+W", () => CloseTab(tab)),
            ];
        }

        /// <summary>The header's second line: where the tab is or what it searched.</summary>
        private static string TabCardMeta(Models.SearchTab tab)
        {
            if (tab.Editor != null) return tab.Editor.IsUntitled ? string.Empty : tab.Editor.FilePath;
            if (tab.HasSearch && !string.IsNullOrEmpty(tab.QueryLabel)) return tab.QueryLabel;
            if (tab.Term == null && !tab.IsBrowsing) return string.Empty;
            string folder = tab.CurrentFolder;
            if (IsPlaceSentinel(folder)) return string.Empty;
            return ArchiveProvider.TrySplit(folder, out string archive, out string entry) && entry.Length > 0
                ? archive + "\\" + entry.Replace('/', '\\')
                : folder;
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
            // 98SE keeps Win98's single light source when the strip moves below the page: the page
            // stays raised (white top and left, #808080 then black on the right and foot) and each
            // tab hangs from it with a white left edge and a dark right edge and foot. Flipping the
            // top-strip chrome would flip the light, so on a bottom strip the tabs' bevel borders
            // are zeroed and TabSurfaceBorder draws their edges instead, and the page foot is the
            // two join lines, black against the tabs and #808080 above it, broken by the
            // selected tab.
            bool retroBottom = bottom && ThemeManager.Current == Theme.SE98;
            string[] tabBevels = { "TabBevelLightThickness", "TabInactiveBevelDarkThickness",
                                   "TabActiveBevelDarkThickness", "TabOuterOutlineThickness" };
            foreach (string key in tabBevels)
                if (retroBottom) pane.TabBar.Resources[key] = new Thickness(0);
                else pane.TabBar.Resources.Remove(key);
            if (retroBottom) pane.TabBar.Resources["TabRetroBottomEdges"] = true;
            else pane.TabBar.Resources.Remove("TabRetroBottomEdges");
            string joinBrush = retroBottom ? "BevelOuterDarkBrush" : "BevelLightBrush";
            pane.RetroTabInnerJoinLeft.SetResourceReference(Border.BackgroundProperty, joinBrush);
            pane.RetroTabInnerJoinRight.SetResourceReference(Border.BackgroundProperty, joinBrush);
            pane.RetroTabFootShade.Visibility = retroBottom ? Visibility.Visible : Visibility.Collapsed;
            pane.TabBandShadow.RenderTransform = tabTransform;
            pane.RetroTabJoinLine.VerticalAlignment = bottom ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            pane.RetroTabJoinLine.Margin = bottom ? new Thickness(0, 1, 0, 0) : new Thickness(0, 0, 0, 1);
            pane.RetroTabInnerJoin.VerticalAlignment = bottom ? VerticalAlignment.Top : VerticalAlignment.Bottom;
            // A 98SE bottom strip's black foot reaches the page's left edge: the shadow owns that
            // corner (Tabs.cs UpdateRetroTabInnerJoin).
            pane.RetroTabInnerJoin.Margin = retroBottom ? new Thickness(0, -1, 0, 0)
                : bottom ? new Thickness(1, -1, 2, 0) : new Thickness(1, 0, 2, -1);
            pane.RetroTabFootShade.Margin = new Thickness(0, -2, 1, 0);

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
            if (!bottom) return;

            pane.ResultsPane.CornerRadius = TabSideCorners(pane, bottom: true);   // Tabs.cs
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
                // Raised page: white top and left, shadow only down the right. The foot is the
                // join lines in the strip, which break for the selected tab.
                pane.PaneBevelOuterDark.BorderThickness = new Thickness(1, 1, 0, 0);
                pane.PaneBevelOuterLight.BorderThickness = new Thickness(0, 0, 1, 0);
                pane.PaneBevelInnerLight.BorderThickness = new Thickness(0, 0, 1, 0);
                // The strip overlaps the page's last two rows and the foot lines take the two
                // above them, so the content stops four rows up and its own sunken edge stays
                // inside the page instead of showing under the foot beside the last tab.
                var padding = pane.ResultsPane.Padding;
                pane.ResultsPane.Padding = new Thickness(padding.Left, padding.Top, padding.Right, 4);
                // The side edges run down to the foot: white and #808080 stop on the gray foot
                // row, and the black right edge meets the black foot, owning that corner.
                pane.PaneBevelOuterDark.Margin = WithBottom(pane.PaneBevelOuterDark.Margin, -1);
                pane.PaneBevelOuterLight.Margin = WithBottom(pane.PaneBevelOuterLight.Margin, -2);
                pane.PaneBevelInnerLight.Margin = WithBottom(pane.PaneBevelInnerLight.Margin, -1);
            }
        }

        private static Thickness WithBottom(Thickness value, double bottom)
            => new Thickness(value.Left, value.Top, value.Right, bottom);

        private static Thickness ReverseVertical(Thickness value)
            => new Thickness(value.Left, value.Bottom, value.Right, value.Top);
    }
}
