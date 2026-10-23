using System;

namespace KillerShell.Services
{
    /// <summary>
    /// How a name gives way when its box gets narrow: it shrinks a little first, then its short
    /// form ("Net" for "Network") takes over, and only when even that will not fit does the name
    /// go, leaving the icon to say what the box is. The Performance Monitor's summary boxes use
    /// it so the reading in the corner never has to give way to the name.
    /// </summary>
    internal static class LabelFit
    {
        internal const double Largest = 18;
        internal const double Smallest = 15;

        /// <summary>The text and size to show in <paramref name="available"/> width, or null for
        /// none. <paramref name="width"/> measures a text at a size.</summary>
        internal static (string Text, double Size)? Choose(double available, string full, string shortName,
            Func<string, double, double> width)
        {
            foreach (string text in new[] { full, shortName })
            {
                if (string.IsNullOrEmpty(text)) continue;
                for (double size = Largest; size >= Smallest; size -= 1)
                    if (width(text, size) <= available) return (text, size);
            }
            return null;
        }
    }
}
