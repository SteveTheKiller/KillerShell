using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace KillerShell.Tools
{
    /// <summary>
    /// The Performance page's summary strip: one row while every tile fits, and when they do not,
    /// the fewest rows that hold them, with the tiles shared out evenly so every row fills the
    /// page's width (five tiles go 3 over 2, never 4 over 1). Every tile is as tall as the tallest.
    /// </summary>
    internal sealed class FillRowsPanel : Panel
    {
        /// <summary>The most tiles a row holds; 0 means no limit, one row.</summary>
        internal int MaxPerRow
        {
            get => _maxPerRow;
            set
            {
                if (_maxPerRow == value) return;
                _maxPerRow = value;
                InvalidateMeasure();
            }
        }
        private int _maxPerRow;

        private UIElement[] Visible() => InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed).ToArray();

        /// <summary>How many tiles each row takes, the longer rows first.</summary>
        private static int[] RowCounts(int count, int maxPerRow)
        {
            if (count == 0) return [];
            int per = maxPerRow <= 0 ? count : Math.Max(1, maxPerRow);
            int rows = (count + per - 1) / per;
            var counts = new int[rows];
            for (int r = 0; r < rows; r++) counts[r] = count / rows + (r < count % rows ? 1 : 0);
            return counts;
        }

        protected override Size MeasureOverride(Size available)
        {
            var tiles = Visible();
            var counts = RowCounts(tiles.Length, _maxPerRow);
            double height = 0, width = 0;
            int i = 0;
            foreach (int n in counts)
            {
                double cell = double.IsInfinity(available.Width) ? double.PositiveInfinity : available.Width / n;
                double rowWidth = 0;
                for (int k = 0; k < n; k++, i++)
                {
                    tiles[i].Measure(new Size(cell, double.PositiveInfinity));
                    height = Math.Max(height, tiles[i].DesiredSize.Height);
                    rowWidth += tiles[i].DesiredSize.Width;
                }
                width = Math.Max(width, rowWidth);
            }
            foreach (var hidden in InternalChildren.Cast<UIElement>().Where(c => c.Visibility == Visibility.Collapsed))
                hidden.Measure(new Size(0, 0));
            return new Size(double.IsInfinity(available.Width) ? width : available.Width, height * counts.Length);
        }

        protected override Size ArrangeOverride(Size final)
        {
            var tiles = Visible();
            var counts = RowCounts(tiles.Length, _maxPerRow);
            if (counts.Length == 0) return final;
            double height = final.Height / counts.Length;
            int i = 0;
            for (int r = 0; r < counts.Length; r++)
            {
                double cell = final.Width / counts[r];
                for (int k = 0; k < counts[r]; k++, i++)
                    tiles[i].Arrange(new Rect(k * cell, r * height, cell, height));
            }
            return final;
        }
    }
}
