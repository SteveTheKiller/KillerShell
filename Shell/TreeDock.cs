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
            FlyoutPlacement.RailOnRight = _treeRight;
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

        // The rail's right-click menu: which side the tree docks to. Its buttons keep their own.
        private void Rail_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            for (var node = e.OriginalSource as DependencyObject; node != null && !ReferenceEquals(node, sender);
                 node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
                if (node is ButtonBase) return;

            var menu = new ContextMenu();
            // A centered caption over the choices, not a disabled row: a MenuItem header sits in
            // the text column after the icon gutter, which left it hanging off-center.
            var caption = new TextBlock
            {
                Text = Loc("Str_Sidebar"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 4),
            };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            caption.SetResourceReference(TextBlock.FontFamilyProperty, "MenuFontFamily");
            caption.SetResourceReference(TextBlock.FontSizeProperty, "MenuFontSize");
            var header = new MenuItem { Header = caption, Focusable = false, IsHitTestVisible = false, IsEnabled = false };
            header.SetResourceReference(FrameworkElement.StyleProperty, "PanelMenuItem");
            menu.Items.Add(header);
            foreach (bool right in new[] { false, true })
            {
                var item = new MenuItem
                {
                    Header = Loc(right ? "Str_Sidebar_Right" : "Str_Sidebar_Left"),
                    IsCheckable = true,
                    IsChecked = right == _treeRight,
                    InputGestureText = right ? "Alt+Shift+Right" : "Alt+Shift+Left",
                    Icon = PaneSideIcon(right ? Dock.Right : Dock.Left),
                };
                item.Click += (_, _) => SelectTreeSide(right);
                menu.Items.Add(item);
            }
            menu.PlacementTarget = (UIElement)sender;
            menu.IsOpen = true;
            e.Handled = true;
        }

        /// <summary>
        /// A small window with its sidebar shaded on the given side, the icon for a side menu's
        /// items. Drawn in the item's text color, read the way a glyph icon reads it, so it
        /// follows the highlight; a checked item shows its checkmark in this slot instead.
        /// </summary>
        private static FrameworkElement PaneSideIcon(Dock side)
        {
            var frame = new Border { BorderThickness = new Thickness(1) };
            frame.SetBinding(Border.BorderBrushProperty, InheritedForeground());
            bool across = side == Dock.Top || side == Dock.Bottom;
            var bar = new Border
            {
                Width = across ? double.NaN : 3,
                Height = across ? 3 : double.NaN,
                HorizontalAlignment = side == Dock.Right ? HorizontalAlignment.Right
                                    : side == Dock.Left ? HorizontalAlignment.Left : HorizontalAlignment.Stretch,
                VerticalAlignment = side == Dock.Bottom ? VerticalAlignment.Bottom
                                  : side == Dock.Top ? VerticalAlignment.Top : VerticalAlignment.Stretch,
            };
            bar.SetBinding(Border.BackgroundProperty, InheritedForeground());
            var icon = new Grid { Width = 12, Height = 10, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            icon.Children.Add(frame);
            icon.Children.Add(bar);
            return icon;
        }

        private static System.Windows.Data.Binding InheritedForeground() => new System.Windows.Data.Binding
        {
            Path = new PropertyPath(System.Windows.Documents.TextElement.ForegroundProperty),
            RelativeSource = System.Windows.Data.RelativeSource.Self,
        };
    }
}
