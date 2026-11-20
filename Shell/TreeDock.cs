using KillerShell.Controls;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace KillerShell.Shell
{
    // Which side the folder tree docks to: left (the default) or right. Partial of MainWindow.
    //
    // The rail stays on the tree's inner lip on either side, so the whole row is a mirror image:
    // ScaleHost's columns flip, which also takes the search panel to the opposite edge from the
    // tree, and the pieces that assume a left-hand tree (the grip, the slide directions, the
    // panes' window-edge inset, the flyout corner) follow _treeRight.
    public partial class MainWindow
    {
        private const string TreeSideKey = "TreePanelSide";
        private bool _treeRight;
        private bool _treeMirrored;   // what ScaleHost currently shows

        /// <summary>Restores the saved side; call once, before InitSearchPanel and InitTreePanel
        /// so their first layout already slides toward the right edges.</summary>
        private void InitTreeSide()
        {
            _treeRight = string.Equals(Services.ThemeManager.GetSetting(TreeSideKey), "Right",
                                       StringComparison.OrdinalIgnoreCase);
            TreeResizeGrip.Loaded += (_, _) => ApplyTreeGripSide();
            if (_treeRight) ApplyTreeSide();
        }

        private void SelectTreeSide(bool right)
        {
            if (right == _treeRight) return;
            _treeRight = right;
            Services.ThemeManager.SetSetting(TreeSideKey, right ? "Right" : "Left");
            ApplyTreeSide();
            ApplyPaneMargins();   // DualPane.cs - the window-edge inset changed sides
        }

        /// <summary>Alt+Shift+Left / Right. The arrow of the side the tree is already on opens
        /// or closes it; the other arrow moves it there.</summary>
        /// <summary>Which side the folder tree docks to. Read by tests.</summary>
        internal bool TreeDockedRight => _treeRight;

        private void TreeSideShortcut(bool right)
        {
            if (right == _treeRight) ToggleTreePanel();
            else SelectTreeSide(right);
        }

        private void ApplyTreeSide()
        {
            LeftPane.QueueSnap();    // the panes move to the other side at the same width
            RightPane.QueueSnap();
            if (_treeMirrored != _treeRight)
            {
                MirrorColumns(ScaleHost);
                _treeMirrored = _treeRight;
            }
            ApplyTreeGripSide();
            FlyoutPlacement.UsePane(PaneHost, _treeRight ? SidebarSide.Right : SidebarSide.Left);
            UpdateTreeChevron();
        }

        /// <summary>Reverses a grid's columns and moves every child to the mirrored column.
        /// Its own inverse, which is why the applied state is tracked separately.</summary>
        private static void MirrorColumns(Grid grid)
        {
            var cols = grid.ColumnDefinitions.Cast<ColumnDefinition>().Reverse().ToList();
            int n = cols.Count;
            grid.ColumnDefinitions.Clear();
            foreach (var col in cols) grid.ColumnDefinitions.Add(col);
            foreach (UIElement child in grid.Children)
                Grid.SetColumn(child, n - Grid.GetColumn(child) - Grid.GetColumnSpan(child));
        }

        // The grip and its 1px line sit on the tree's inner edge, against the rail.
        private void ApplyTreeGripSide()
        {
            var side = _treeRight ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            TreeResizeGrip.HorizontalAlignment = side;
            if (TreeResizeGrip.Template?.FindName("hint", TreeResizeGrip) is Rectangle hint)
                hint.HorizontalAlignment = side;
        }

        // The rail's right-click card: which side the tree docks to. Its buttons keep their own.
        private void Rail_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            for (var node = e.OriginalSource as DependencyObject; node != null && !ReferenceEquals(node, sender);
                 node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
                if (node is ButtonBase) return;

            // A small strip card: one checked tile per side, the one in use lit, with the kit's side glyphs
            // (SidebarSide.TileGlyph: MDL2 DockLeft and DockRight).
            var menu = Controls.CardMenus.TileCard();
            Controls.CardMenus.WatchKeyboardStart(menu);
            menu.Items.Add(Controls.CardMenus.Tiles(menu, new[]
            {
                Controls.CardMenus.Tile("treeleft", 0xE90C, "Str_Sidebar_Left", null, "Alt+Shift+Left", () => SelectTreeSide(false), on: !_treeRight),
                Controls.CardMenus.Tile("treeright", 0xE90D, "Str_Sidebar_Right", null, "Alt+Shift+Right", () => SelectTreeSide(true), on: _treeRight),
            }));
            menu.PlacementTarget = (UIElement)sender;
            menu.IsOpen = true;
            e.Handled = true;
        }
    }
}
