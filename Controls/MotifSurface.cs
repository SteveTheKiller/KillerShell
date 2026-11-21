using System.Windows;

namespace KillerShell.Controls
{
    /// <summary>
    /// For surfaces that paint their own opaque background (the terminal screen, the text editor): true while the theme's
    /// ink shows, so they leave their fill off and let the pinned ink layer behind them through. Mirrors the checks
    /// <see cref="ThemeMotif"/> makes before it paints, so the fill and the ink never both go missing or both draw.
    /// </summary>
    internal static class MotifSurface
    {
        internal static bool ShowsInk(FrameworkElement? element)
        {
            object? motif = element != null ? element.TryFindResource("ThemeMotif") : Application.Current?.TryFindResource("ThemeMotif");
            if (motif is not true || SystemParameters.HighContrast) return false;
            return element == null || !KillerFamily.Accessibility.HighContrastResources.GetIsActive(element);
        }
    }
}
