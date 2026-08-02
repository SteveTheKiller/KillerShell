using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerShell.Services;

// KillerUI / Grunge - title-bar theme + accent pickers. Partial of MainWindow.
// MainWindow.xaml must provide: ThemeButton, ThemePopup (child = flyout Border),
// ThemeSwatches (Buttons Tag=Theme name, Click=ThemeSwatch_Click),
// AccentSwatches (Buttons Tag=Accent name, Click=AccentSwatch_Click), optional AccentLabel.
namespace KillerShell.Shell
{
    public partial class MainWindow
    {
        private void ThemeSwatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string name && Enum.TryParse<Theme>(name, out var theme))
            {
                ThemeManager.Apply(theme);
                ApplyThemeBorder(this);   // retint the DWM frame border to the new palette
                UpdateThemeSwatchSelection();
                UpdateAccentSwatches();

                // A shell resolves its colors ONCE, when its palette is built - it has to, since
                // it paints thousands of cells a frame and cannot carry a DynamicResource per
                // cell. So a theme switch has to tell it to rebuild, or every open terminal
                // keeps the colors of the theme it was opened under (TerminalTabs.cs).
                RefreshTerminalThemes();

                // An open document resolves its colors the same way and for the same reason
                // (EditorTabs.cs, Editing/EditorControl.ApplyTheme).
                RefreshEditorThemes();
            }
        }

        private void AccentSwatch_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button b && b.Tag is string name && Enum.TryParse<Accent>(name, out var accent))
            {
                ThemeManager.ApplyAccent(ThemeManager.Current, accent);
                UpdateThemeSwatchSelection();
                UpdateAccentSwatches();

                // The accent is a shell's cursor and selection color, so this needs the rebuild
                // as much as a full theme switch does - and it is a document's caret, selection
                // and current-line wash for the same reason.
                RefreshTerminalThemes();
                RefreshEditorThemes();
            }
        }

        private void ThemeButton_Click(object sender, RoutedEventArgs e)
        {
            if (FindName("ThemePopup") is System.Windows.Controls.Primitives.Popup p)
            {
                p.IsOpen = !p.IsOpen;
                if (p.IsOpen && p.Child is UIElement child) Anim.FadeIn(child);
            }
        }

        private void UpdateThemeSwatchSelection()
            => HighlightSwatches(FindName("ThemeSwatches") as Panel, ThemeManager.Current.ToString());

        private void UpdateAccentSwatches()
        {
            if (FindName("AccentSwatches") is not Panel panel) return;
            var t = ThemeManager.Current;
            bool hasAccents = t == Theme.Dark || t == Theme.Light || t == Theme.Black;
            var vis = hasAccents ? Visibility.Visible : Visibility.Collapsed;
            panel.Visibility = vis;
            if (FindName("AccentLabel") is UIElement lbl) lbl.Visibility = vis;
            if (hasAccents)
                HighlightSwatches(panel, ThemeManager.AccentChoiceFor(t).ToString());
        }

        private void HighlightSwatches(Panel? panel, string current)
        {
            if (panel == null) return;
            var activeRing = TryFindResource("PrimaryBrush") as Brush ?? Brushes.White;
            var idleRing   = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            foreach (var child in panel.Children)
            {
                if (child is not Button b || b.Tag is not string name) continue;
                bool active = name == current;
                b.BorderBrush     = active ? activeRing : idleRing;
                b.BorderThickness = new Thickness(active ? 2 : 1);
            }
        }
    }
}
