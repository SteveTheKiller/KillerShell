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
        private const string PanesLeftRightIcon = "M0,0 H6 V14 H0 Z M8,0 H14 V14 H8 Z";
        private const string PanesTopBottomIcon = "M0,0 H14 V6 H0 Z M0,8 H14 V14 H0 Z";

        private static MenuItem LayoutMenuItem(string label, string shortcut, string geometry, Action action)
        {
            var icon = new Path { Width = 14, Height = 14, Stretch = Stretch.Uniform,
                StrokeThickness = 1, Data = Geometry.Parse(geometry) };
            icon.SetBinding(Shape.StrokeProperty, new Binding("Foreground") {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(MenuItem), 1) });
            var item = new MenuItem { InputGestureText = shortcut, Icon = icon };
            item.SetResourceReference(HeaderedItemsControl.HeaderProperty, label);
            item.Click += (_, _) => action();
            return item;
        }

        private void PopulateDetailsPaneMenu(FilePane pane)
        {
            var menu = pane.DetailsPaneBtn.ContextMenu;
            menu.Items.Clear();
            menu.Items.Add(LayoutMenuItem("Str_Layout_DetailsBottom", "Ctrl+Alt+Down", DetailsBottomIcon,
                () => SetDetailsPanePosition(pane, 1)));
            menu.Items.Add(LayoutMenuItem("Str_Layout_DetailsRight", "Ctrl+Alt+Right", DetailsRightIcon,
                () => SetDetailsPanePosition(pane, 2)));
            menu.Items.Add(LayoutMenuItem("Str_Layout_DetailsOff", "Ctrl+Alt+0", DetailsOffIcon,
                () => SetDetailsPanePosition(pane, 0)));
        }

        private static void UpdateDetailsPaneButton(FilePane pane)
        {
            pane.DetailsPaneGlyph.Data = Geometry.Parse(!pane.DetailsPaneOpen ? DetailsOffIcon
                : pane.DetailsPaneOnRight ? DetailsRightIcon : DetailsBottomIcon);
            pane.DetailsPaneBtn.SetResourceReference(FrameworkElement.ToolTipProperty,
                !pane.DetailsPaneOpen ? "Str_TT_DetailsBottom" : pane.DetailsPaneOnRight ? "Str_TT_DetailsOff" : "Str_TT_DetailsRight");
        }

        private void PaneOrientationMenu_Opened(object sender, RoutedEventArgs e)
        {
            var menu = (ContextMenu)sender;
            menu.Items.Clear();
            menu.Items.Add(LayoutMenuItem("Str_Layout_LeftRight", "Ctrl+Alt+Left", PanesLeftRightIcon,
                () => SetPaneOrientation(true)));
            menu.Items.Add(LayoutMenuItem("Str_Layout_TopBottom", "Ctrl+Alt+Up", PanesTopBottomIcon,
                () => SetPaneOrientation(false)));
        }

        private void SetPaneOrientation(bool sideBySide)
        {
            _paneSideBySide = sideBySide;
            if (!DualPane) OpenSecondPane();
            else ApplyPaneLayout();
        }
    }
}
