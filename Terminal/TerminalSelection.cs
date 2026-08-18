using System;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

// Selecting and copying. Part of TerminalControl.
//
// Anchored in BUFFER coordinates, not screen coordinates: an absolute line index that counts
// from the top of scrollback. A selection made and then scrolled past would otherwise slide
// across the text it was pointing at, because the same screen row means a different line the
// moment anything scrolls.
namespace KillerShell.Terminal
{
    internal sealed partial class TerminalControl
    {
        private bool _selecting;
        private bool _hasSelection;
        private int _selAnchorLine, _selAnchorCol;      // where the drag began
        private int _selHeadLine, _selHeadCol;          // where it is now

        // ═══════════════════════════════════════════════════════════
        //  HIT TESTING
        // ═══════════════════════════════════════════════════════════
        /// <summary>Pointer position to an absolute line and column.</summary>
        private void CellAt(Point p, out int line, out int col)
        {
            int first = Math.Max(0, _buf.ScrollbackCount - _scroll);
            int row = _cellH > 0 ? (int)(p.Y / _cellH) : 0;
            line = first + Math.Max(0, Math.Min(_buf.Rows - 1, row));

            // Rounded, not truncated: a click past the middle of a cell belongs to the NEXT
            // boundary, which is what makes dragging feel like it selects what you crossed.
            col = _cellW > 0 ? (int)Math.Round((p.X - LeftInset) / _cellW) : 0;
            col = Math.Max(0, Math.Min(_buf.Cols, col));
        }

        private void SelectionMouseDown(MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            var p = e.GetPosition(this);

            if (e.ClickCount == 2) { SelectWordAt(p); return; }
            if (e.ClickCount >= 3) { SelectLineAt(p); return; }

            CellAt(p, out _selAnchorLine, out _selAnchorCol);
            _selHeadLine = _selAnchorLine;
            _selHeadCol = _selAnchorCol;
            _selecting = true;
            _hasSelection = false;
            CaptureMouse();
            InvalidateVisual();
        }

        private void SelectionMouseMove(MouseEventArgs e)
        {
            if (!_selecting || e.LeftButton != MouseButtonState.Pressed) return;

            CellAt(e.GetPosition(this), out _selHeadLine, out _selHeadCol);
            _hasSelection = _selHeadLine != _selAnchorLine || _selHeadCol != _selAnchorCol;
            InvalidateVisual();
        }

        private void SelectionMouseUp()
        {
            if (!_selecting) return;
            _selecting = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            InvalidateVisual();
        }

        /// <summary>
        /// Word under the pointer. Word characters are letters, digits and the handful of
        /// punctuation that appears INSIDE things you want whole - a path, a flag, a URL - so
        /// double-clicking a path gives you the path rather than one segment of it.
        /// </summary>
        private void SelectWordAt(Point p)
        {
            CellAt(p, out int line, out int col);
            var cells = _buf.LineAt(line);
            col = Math.Min(col, cells.Length - 1);
            if (col < 0) return;

            int a = col, b = col;
            while (a > 0 && IsWordChar(cells[a - 1].Ch)) a--;
            while (b < cells.Length - 1 && IsWordChar(cells[b + 1].Ch)) b++;

            _selAnchorLine = _selHeadLine = line;
            _selAnchorCol = a;
            _selHeadCol = b + 1;
            _hasSelection = true;
            _selecting = false;
            InvalidateVisual();
        }

        private static bool IsWordChar(int cp)
        {
            if (cp == 0 || cp == ' ') return false;
            char c = (char)cp;
            return char.IsLetterOrDigit(c) || "_-.\\/:~@+=%#".IndexOf(c) >= 0;
        }

        private void SelectLineAt(Point p)
        {
            CellAt(p, out int line, out _);
            _selAnchorLine = _selHeadLine = line;
            _selAnchorCol = 0;
            _selHeadCol = _buf.Cols;
            _hasSelection = true;
            _selecting = false;
            InvalidateVisual();
        }

        internal void SelectAll()
        {
            _selAnchorLine = 0;
            _selAnchorCol = 0;
            _selHeadLine = _buf.TotalLines - 1;
            _selHeadCol = _buf.Cols;
            _hasSelection = true;
            InvalidateVisual();
        }

        internal void ClearSelection()
        {
            if (!_hasSelection) return;
            _hasSelection = false;
            InvalidateVisual();
        }

        internal bool HasSelection => _hasSelection;

        // ═══════════════════════════════════════════════════════════
        //  ORDER
        // ═══════════════════════════════════════════════════════════
        // The anchor is where the drag STARTED, which may be after the head. Everything below
        // wants them in reading order, so they are normalized once here.
        private void Ordered(out int l1, out int c1, out int l2, out int c2)
        {
            if (_selAnchorLine < _selHeadLine ||
                (_selAnchorLine == _selHeadLine && _selAnchorCol <= _selHeadCol))
            {
                l1 = _selAnchorLine; c1 = _selAnchorCol;
                l2 = _selHeadLine;   c2 = _selHeadCol;
            }
            else
            {
                l1 = _selHeadLine;   c1 = _selHeadCol;
                l2 = _selAnchorLine; c2 = _selAnchorCol;
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  RENDER
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Wash the selected cells. Drawn OVER the glyphs at low alpha rather than under them,
        /// so the text stays readable instead of being repainted in an inverse color - a
        /// terminal's own colors are the information here.
        /// </summary>
        private void DrawSelection(DrawingContext dc, int firstVisible)
        {
            if (!_hasSelection) return;

            Ordered(out int l1, out int c1, out int l2, out int c2);

            var brush = new SolidColorBrush(_palette.Selection);
            brush.Freeze();

            for (int r = 0; r < _buf.Rows; r++)
            {
                int line = firstVisible + r;
                if (line < l1 || line > l2) continue;

                int from = line == l1 ? c1 : 0;
                int to   = line == l2 ? c2 : _buf.Cols;
                if (to <= from) continue;

                dc.DrawRectangle(brush, null,
                    new Rect(from * _cellW, r * _cellH, (to - from) * _cellW, _cellH));
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  COPY
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Selected text to the clipboard. Returns false when there was nothing selected, which
        /// is what lets Ctrl+C fall through to interrupt.
        /// </summary>
        internal bool CopySelection()
        {
            if (!_hasSelection) return false;

            Ordered(out int l1, out int c1, out int l2, out int c2);
            var sb = new StringBuilder();

            for (int line = l1; line <= l2 && line < _buf.TotalLines; line++)
            {
                var cells = _buf.LineAt(line);
                int from = line == l1 ? c1 : 0;
                int to   = Math.Min(line == l2 ? c2 : cells.Length, cells.Length);

                var row = new StringBuilder();
                for (int c = from; c < to; c++)
                    row.Append(cells[c].Ch == 0 ? ' ' : (char)cells[c].Ch);

                // Trailing blanks are padding, not content: a terminal line is always Cols wide
                // whether or not anything was written to the end of it, and pasting that
                // padding back into a shell is never what anyone wants.
                sb.Append(row.ToString().TrimEnd());
                if (line < l2) sb.Append("\r\n");
            }

            string text = sb.ToString();
            if (text.Length == 0) return false;

            try { Clipboard.SetText(text); }
            catch { return false; }        // another app holding the clipboard

            return true;
        }
    }
}
