using System;
using System.Globalization;
using System.Windows;

// The size and place a new window opens at: the last window's, as "left,top,width,height,max" in
// DIPs, under HKCU\Software\KillerShell WindowBounds (Shell/WindowBounds.cs). Invariant numbers,
// so a language with comma decimals reads back what it wrote.
namespace KillerShell.Services
{
    internal static class SavedWindowBounds
    {
        internal const string Setting = "WindowBounds";

        internal static string Format(Rect bounds, bool maximized) => string.Join(",",
            bounds.X.ToString("0.##", CultureInfo.InvariantCulture),
            bounds.Y.ToString("0.##", CultureInfo.InvariantCulture),
            bounds.Width.ToString("0.##", CultureInfo.InvariantCulture),
            bounds.Height.ToString("0.##", CultureInfo.InvariantCulture),
            maximized ? "1" : "0");

        internal static bool TryParse(string? saved, out Rect bounds, out bool maximized)
        {
            bounds = Rect.Empty;
            maximized = false;
            string[] parts = (saved ?? string.Empty).Split(',');
            if (parts.Length != 5) return false;
            var values = new double[4];
            for (int i = 0; i < 4; i++)
                if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])
                    || double.IsNaN(values[i]) || double.IsInfinity(values[i]))
                    return false;
            if (values[2] <= 0 || values[3] <= 0) return false;
            bounds = new Rect(values[0], values[1], values[2], values[3]);
            maximized = parts[4] == "1";
            return true;
        }
    }
}
