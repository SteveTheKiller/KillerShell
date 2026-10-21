using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

// The screen: what the VT stream means, held as a grid of cells plus scrollback.
//
// Everything here runs on the UI thread. The pipe is read on a background thread, but the
// bytes are handed over and PARSED on the dispatcher (TerminalControl), so the grid needs no
// lock and the renderer can never catch it half updated. Shell output is small enough for
// that to be free; the one case that is not, dumping a huge file, is handled by draining the
// queue on a timer instead of once per read.
namespace KillerShell.Terminal
{
    [Flags]
    internal enum CellFlags : byte
    {
        None = 0, Bold = 1, Faint = 2, Italic = 4, Underline = 8,
        Blink = 16, Inverse = 32, Hidden = 64, Strike = 128,
    }

    internal struct Cell
    {
        public int Ch;          // codepoint; 0 is an empty cell
        public int Fg, Bg;      // see TerminalBuffer.DefaultColor / Rgb
        public CellFlags Flags;
        public byte Width;
        public bool IsContinuation;
        public string? Combining;
        public string Text => IsContinuation ? string.Empty : (Ch == 0 ? " " : char.ConvertFromUtf32(Ch)) + Combining;
    }

    internal sealed class TerminalBuffer : IVtHandler
    {
        /// <summary>Use the palette's foreground/background rather than a specific color.</summary>
        public const int DefaultColor = -1;

        /// <summary>Truecolor marker. Below this a color is a palette index, 0 to 255.</summary>
        public const int RgbFlag = 0x1000000;

        public static int Rgb(int r, int g, int b) => RgbFlag | (r << 16) | (g << 8) | b;

        // Replies are built from a CODEPOINT, the same convention the MDL2 glyphs elsewhere in
        // this project use. A literal escape byte in a string literal is invisible in an editor,
        // cannot be grepped, and does not survive an encoding round trip.
        private static readonly string Csi = ((char)0x1B).ToString() + "[";

        // ── Geometry ─────────────────────────────────────────────
        public int Cols { get; private set; }
        public int Rows { get; private set; }

        private Cell[][] _screen = [];
        private Cell[][]? _altScreen;                       // non-null while the alt buffer is up
        private readonly List<Cell[]> _scrollback = [];
        private readonly ConditionalWeakTable<Cell[], object> _lineIdentities = new();

        internal object LineIdentity(Cell[] line) => _lineIdentities.GetValue(line, _ => new object());

        /// <summary>Cap on remembered lines. 5000 x 200 cells is a few MB, which is fine.</summary>
        public int ScrollbackLimit { get; set; } = 5000;

        public int ScrollbackCount => _scrollback.Count;

        /// <summary>Bumped on every change, so the renderer can skip an unchanged frame.</summary>
        public int Version { get; private set; }

        // ── Cursor and state ─────────────────────────────────────
        public int CursorRow { get; private set; }
        public int CursorCol { get; private set; }
        public bool CursorVisible { get; private set; } = true;
        public bool CursorBlink { get; private set; } = true;
        public int CursorShape { get; private set; }        // 0 block, 1 underline, 2 bar

        private int _curFg = DefaultColor, _curBg = DefaultColor;
        private CellFlags _curFlags;

        private int _savedRow, _savedCol, _savedFg = DefaultColor, _savedBg = DefaultColor;
        private CellFlags _savedFlags;

        private int _top, _bottom;                          // scroll region, inclusive, 0 based
        private bool _autoWrap = true;
        private bool _originMode;
        private bool _insertMode;
        private bool _pendingWrap;                          // cursor is parked past the last column
        private readonly HashSet<int> _tabStops = [];

        /// <summary>Arrow keys send SS3 instead of CSI. PSReadLine turns this on.</summary>
        public bool AppCursorKeys { get; private set; }

        /// <summary>Paste is wrapped in brackets so the shell can tell it from typing.</summary>
        public bool BracketedPaste { get; private set; }

        public bool AltScreen => _altScreen != null;

        /// <summary>Set from OSC 0/2. The tab title follows it.</summary>
        public string Title { get; private set; } = string.Empty;

        /// <summary>Set from OSC 7 or OSC 9;9. Lets the tab track the shell's directory.</summary>
        public string WorkingDirectory { get; private set; } = string.Empty;

        public event Action<string>? TitleChanged;
        public event Action<string>? DirectoryChanged;

        /// <summary>
        /// Replies the terminal owes the shell, complete sequences including the escape, so the
        /// control can pass them straight to the pty without knowing what they are.
        /// </summary>
        public event Action<string>? Respond;

        public TerminalBuffer(int cols, int rows) => Resize(cols, rows);

        // ═══════════════════════════════════════════════════════════
        //  ACCESS
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// A row for rendering. <paramref name="index"/> counts from the top of scrollback, so
        /// row (ScrollbackCount + n) is screen line n.
        /// </summary>
        public Cell[] LineAt(int index)
        {
            if (index < 0) return _screen[0];
            if (index < _scrollback.Count) return _scrollback[index];
            int r = index - _scrollback.Count;
            return r < Rows ? _screen[r] : _screen[Rows - 1];
        }

        public int TotalLines => _scrollback.Count + Rows;

        // ═══════════════════════════════════════════════════════════
        //  RESIZE
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// Rows are kept and re-padded rather than reflowed. Reflowing wrapped lines on resize
        /// is the single largest source of bugs in terminal emulators, and Windows consoles do
        /// not do it either, so the shell's own idea of the screen stays in step with ours.
        /// </summary>
        public void Resize(int cols, int rows)
        {
            cols = Math.Max(1, cols);
            rows = Math.Max(1, rows);
            if (cols == Cols && rows == Rows) return;

            int oldRows = Rows;
            int cursorAfter = CursorCol + (_pendingWrap ? 1 : 0);

            if (_altScreen == null)
            {
                int shift = MainShift(_screen, oldRows, rows, CursorRow);
                _screen = Regrid(_screen, oldRows, cols, rows, shift, toScrollback: true);
                CursorRow -= shift;
                _savedRow = Math.Max(0, _savedRow - shift);
            }
            else
            {
                // A full-screen program owns the alternate screen and redraws it for the new size,
                // so it is cut from the top as before and never becomes scrollback. The main
                // screen kept behind it moves like the main screen, around its saved cursor.
                _screen = Regrid(_screen, oldRows, cols, rows, 0, toScrollback: false);
                int shift = MainShift(_altScreen, oldRows, rows, _savedRow);
                _altScreen = Regrid(_altScreen, oldRows, cols, rows, shift, toScrollback: true);
                _savedRow = Math.Max(0, _savedRow - shift);
            }

            Cols = cols;
            Rows = rows;
            _top = 0;
            _bottom = rows - 1;
            CursorRow = Math.Min(CursorRow, rows - 1);
            CursorCol = Math.Min(cursorAfter, cols - 1);
            _pendingWrap = _pendingWrap && cursorAfter >= cols;
            ResetTabs();
            Version++;
        }

        /// <summary>
        /// How many rows a main screen moves up to fit <paramref name="rows"/>. The cursor row and
        /// anything written below it stay on screen when they can; the rows that no longer fit
        /// are the oldest ones at the top, which is where the console itself drops them. Growing
        /// never moves anything.
        /// </summary>
        private static int MainShift(Cell[][] grid, int oldRows, int rows, int cursorRow)
        {
            int lastUsed = Math.Min(cursorRow, oldRows - 1);
            for (int r = oldRows - 1; r > lastUsed; r--)
                if (!IsBlank(grid[r])) { lastUsed = r; break; }
            return Math.Max(0, Math.Min(lastUsed - (rows - 1), cursorRow));
        }

        private static bool IsBlank(Cell[] line)
        {
            foreach (var cell in line)
                if (cell.Ch != 0 || cell.Combining != null || cell.Bg != DefaultColor) return false;
            return true;
        }

        /// <summary>
        /// A copy of <paramref name="old"/> at the new size, starting at row <paramref name="shift"/>.
        /// Rows above it move into scrollback whole (the same arrays, so selections and
        /// accessibility ranges stay attached to them) when <paramref name="toScrollback"/> is set.
        /// Copied rows carry their identity to the new arrays.
        /// </summary>
        private Cell[][] Regrid(Cell[][] old, int oldRows, int cols, int rows, int shift, bool toScrollback)
        {
            if (toScrollback && shift > 0)
            {
                for (int r = 0; r < shift; r++) _scrollback.Add(old[r]);
                if (_scrollback.Count > ScrollbackLimit)
                    _scrollback.RemoveRange(0, _scrollback.Count - ScrollbackLimit);
            }
            var grid = New(cols, rows);
            for (int r = 0; r < Math.Min(oldRows - shift, rows); r++)
            {
                Array.Copy(old[r + shift], grid[r], Math.Min(Cols, cols));
                NormalizeLine(grid[r]);
                if (_lineIdentities.TryGetValue(old[r + shift], out object identity))
                    _lineIdentities.Add(grid[r], identity);
            }
            return grid;
        }

        private static Cell[][] New(int cols, int rows)
        {
            var g = new Cell[rows][];
            for (int r = 0; r < rows; r++) g[r] = NewLine(cols);
            return g;
        }

        private static Cell[] NewLine(int cols)
        {
            var line = new Cell[cols];
            for (int c = 0; c < cols; c++) { line[c].Fg = DefaultColor; line[c].Bg = DefaultColor; }
            return line;
        }

        private void ResetTabs()
        {
            _tabStops.Clear();
            for (int c = 8; c < Cols; c += 8) _tabStops.Add(c);
        }

        // ═══════════════════════════════════════════════════════════
        //  PRINT
        // ═══════════════════════════════════════════════════════════
        public void Print(int cp)
        {
            if (cp == 0) return;
            int width = UnicodeWidth.Of(cp);
            if (width == 0)
            {
                var previousLine = _screen[CursorRow];
                int previous = _pendingWrap ? CursorCol : CursorCol - 1;
                if (previous >= 0 && previousLine[previous].IsContinuation) previous--;
                if (previous >= 0 && previousLine[previous].Ch != 0)
                {
                    string suffix = char.ConvertFromUtf32(cp);
                    if ((previousLine[previous].Combining?.Length ?? 0) + suffix.Length <= 64)
                    {
                        previousLine[previous].Combining += suffix;
                        Version++;
                    }
                }
                return;
            }

            // Deferred wrap: after writing the last column the cursor STAYS there and only
            // wraps when the next character arrives. Wrapping eagerly puts the cursor on the
            // next line too early, and everything that then asks where it is gets a wrong
            // answer - which is how prompts end up a line adrift.
            if (_pendingWrap)
            {
                if (_autoWrap)
                {
                    CursorCol = 0;
                    LineFeed();
                }
                _pendingWrap = false;
            }

            if (CursorCol >= Cols) CursorCol = Cols - 1;
            if (width == 2 && CursorCol == Cols - 1)
            {
                if (_autoWrap && Cols > 1) { CursorCol = 0; LineFeed(); }
                else { cp = 0xFFFD; width = 1; }
            }

            var line = _screen[CursorRow];
            if (_insertMode) InsertChars(width);
            ClearRun(line, CursorCol, CursorCol + width - 1);

            line[CursorCol].Ch = cp;
            line[CursorCol].Fg = _curFg;
            line[CursorCol].Bg = _curBg;
            line[CursorCol].Flags = _curFlags;
            line[CursorCol].Width = (byte)width;
            line[CursorCol].IsContinuation = false;
            line[CursorCol].Combining = null;
            if (width == 2)
            {
                line[CursorCol + 1] = line[CursorCol];
                line[CursorCol + 1].Ch = 0;
                line[CursorCol + 1].Width = 0;
                line[CursorCol + 1].IsContinuation = true;
            }

            int next = CursorCol + width;
            _pendingWrap = next >= Cols;
            CursorCol = Math.Min(next, Cols - 1);

            Version++;
        }

        // ═══════════════════════════════════════════════════════════
        //  C0 CONTROLS
        // ═══════════════════════════════════════════════════════════
        public void Execute(byte b)
        {
            switch (b)
            {
                case 0x07: break;                                   // BEL, deliberately silent
                case 0x08:                                          // BS
                    if (_pendingWrap) _pendingWrap = false;
                    else if (CursorCol > 0) CursorCol--;
                    break;
                case 0x09: Tab(); break;
                case 0x0A: case 0x0B: case 0x0C:                    // LF, VT, FF
                    _pendingWrap = false;
                    LineFeed();
                    break;
                case 0x0D: CursorCol = 0; _pendingWrap = false; break;
                default: return;
            }
            Version++;
        }

        private void Tab()
        {
            _pendingWrap = false;
            int c = CursorCol + 1;
            while (c < Cols - 1 && !_tabStops.Contains(c)) c++;
            CursorCol = Math.Min(c, Cols - 1);
        }

        private void LineFeed()
        {
            if (CursorRow == _bottom) ScrollUp(1);
            else if (CursorRow < Rows - 1) CursorRow++;
        }

        /// <summary>
        /// Scroll the region up. Lines leaving the TOP of a full-screen region go to
        /// scrollback; lines leaving a smaller region are dropped, because they are part of
        /// something being animated in place and would be noise in the history.
        /// </summary>
        private void ScrollUp(int n)
        {
            bool wholeScreen = _top == 0 && _bottom == Rows - 1 && _altScreen == null;
            for (int i = 0; i < n; i++)
            {
                var gone = _screen[_top];
                if (wholeScreen)
                {
                    _scrollback.Add(gone);
                    if (_scrollback.Count > ScrollbackLimit)
                        _scrollback.RemoveRange(0, _scrollback.Count - ScrollbackLimit);
                }
                for (int r = _top; r < _bottom; r++) _screen[r] = _screen[r + 1];
                _screen[_bottom] = wholeScreen ? NewLine(Cols) : Blank(gone);
            }
            Version++;
        }

        private void ScrollDown(int n)
        {
            for (int i = 0; i < n; i++)
            {
                var gone = _screen[_bottom];
                for (int r = _bottom; r > _top; r--) _screen[r] = _screen[r - 1];
                _screen[_top] = Blank(gone);
            }
            Version++;
        }

        // Reuses the array rather than allocating: a full-screen scroll happens on every line
        // of a long output, and a fresh Cell[] each time is pure garbage churn.
        private Cell[] Blank(Cell[] reuse)
        {
            ClearCells(reuse, 0, reuse.Length - 1);
            return reuse;
        }

        // ═══════════════════════════════════════════════════════════
        //  ESC
        // ═══════════════════════════════════════════════════════════
        public void EscDispatch(char final, char inter)
        {
            switch (final)
            {
                case '7': SaveCursor(); break;
                case '8': RestoreCursor(); break;
                case 'D': _pendingWrap = false; LineFeed(); break;
                case 'E': _pendingWrap = false; CursorCol = 0; LineFeed(); break;
                case 'M':                                            // reverse index
                    _pendingWrap = false;
                    if (CursorRow == _top) ScrollDown(1);
                    else if (CursorRow > 0) CursorRow--;
                    break;
                case 'H': _tabStops.Add(CursorCol); break;
                case 'c': FullReset(); break;
            }
            Version++;
        }

        private void SaveCursor()
        {
            _savedRow = CursorRow; _savedCol = CursorCol;
            _savedFg = _curFg; _savedBg = _curBg; _savedFlags = _curFlags;
        }

        private void RestoreCursor()
        {
            CursorRow = Math.Min(_savedRow, Rows - 1);
            CursorCol = Math.Min(_savedCol, Cols - 1);
            _curFg = _savedFg; _curBg = _savedBg; _curFlags = _savedFlags;
            _pendingWrap = false;
        }

        private void FullReset()
        {
            _screen = New(Cols, Rows);
            _altScreen = null;
            _scrollback.Clear();
            CursorRow = CursorCol = 0;
            _curFg = _curBg = DefaultColor;
            _curFlags = CellFlags.None;
            _top = 0; _bottom = Rows - 1;
            _autoWrap = true; _originMode = false; _insertMode = false;
            CursorVisible = true;
            AppCursorKeys = false; BracketedPaste = false;
            ResetTabs();
        }

        // ═══════════════════════════════════════════════════════════
        //  CSI
        // ═══════════════════════════════════════════════════════════
        public void CsiDispatch(char final, int[] p, char prefix, char inter)
        {
            int P(int i, int def = 1)
            {
                int v = i < p.Length ? p[i] : 0;
                return v == 0 ? def : v;
            }

            if (prefix == '?')
            {
                if (final == 'h' || final == 'l') { PrivateMode(p, final == 'h'); Version++; }
                return;
            }

            switch (final)
            {
                case 'A': _pendingWrap = false; CursorRow = Math.Max(RowFloor(), CursorRow - P(0)); break;
                case 'B': _pendingWrap = false; CursorRow = Math.Min(RowCeil(), CursorRow + P(0)); break;
                case 'C': _pendingWrap = false; CursorCol = Math.Min(Cols - 1, CursorCol + P(0)); break;
                case 'D': _pendingWrap = false; CursorCol = Math.Max(0, CursorCol - P(0)); break;
                case 'E': _pendingWrap = false; CursorCol = 0; CursorRow = Math.Min(RowCeil(), CursorRow + P(0)); break;
                case 'F': _pendingWrap = false; CursorCol = 0; CursorRow = Math.Max(RowFloor(), CursorRow - P(0)); break;
                case 'G': case '`': _pendingWrap = false; CursorCol = Clamp(P(0) - 1, Cols); break;
                case 'd': _pendingWrap = false; CursorRow = Clamp(P(0) - 1, Rows); break;

                case 'H': case 'f':
                    _pendingWrap = false;
                    CursorRow = Clamp((_originMode ? _top : 0) + P(0) - 1, Rows);
                    CursorCol = Clamp(P(1) - 1, Cols);
                    break;

                case 'J': EraseDisplay(p.Length > 0 ? p[0] : 0); break;
                case 'K': EraseLine(p.Length > 0 ? p[0] : 0); break;

                case 'L': InsertLines(P(0)); break;
                case 'M': DeleteLines(P(0)); break;
                case 'P': DeleteChars(P(0)); break;
                case '@': InsertChars(P(0)); break;
                case 'X': EraseChars(P(0)); break;

                case 'S': ScrollUp(P(0)); break;
                case 'T': ScrollDown(P(0)); break;

                case 'b':                                            // repeat the last glyph
                    {
                        var line = _screen[CursorRow];
                        int previous = _pendingWrap ? CursorCol : CursorCol - 1;
                        if (previous >= 0 && line[previous].IsContinuation) previous--;
                        if (previous >= 0 && line[previous].Ch != 0)
                        {
                            var cell = line[previous];
                            for (int i = 0; i < P(0); i++)
                            {
                                Print(cell.Ch);
                                string suffix = cell.Combining ?? string.Empty;
                                for (int j = 0; j < suffix.Length; j++)
                                {
                                    int scalar = char.ConvertToUtf32(suffix, j);
                                    Print(scalar);
                                    if (scalar > 0xFFFF) j++;
                                }
                            }
                        }
                    }
                    break;

                case 'm': Sgr(p); break;

                case 'r':
                    _top = Clamp(P(0) - 1, Rows);
                    _bottom = Clamp(P(1, Rows) - 1, Rows);
                    if (_bottom <= _top) { _top = 0; _bottom = Rows - 1; }
                    CursorRow = _originMode ? _top : 0;
                    CursorCol = 0;
                    break;

                case 'g':
                    if (P(0, 0) == 3) _tabStops.Clear();
                    else _tabStops.Remove(CursorCol);
                    break;

                case 'n':                                            // device status report
                    if (P(0, 0) == 6)
                        Respond?.Invoke(Csi + (CursorRow + 1) + ";" + (CursorCol + 1) + "R");
                    else if (P(0, 0) == 5)
                        Respond?.Invoke(Csi + "0n");
                    break;

                case 'c':                                            // "I am a VT100 with AVO"
                    Respond?.Invoke(Csi + "?1;2c");
                    break;

                case 'h': if (P(0, 0) == 4) _insertMode = true;  break;
                case 'l': if (P(0, 0) == 4) _insertMode = false; break;

                case 'q':                                            // DECSCUSR, cursor shape
                    if (inter == ' ')
                    {
                        int s = P(0, 1);
                        CursorBlink = s == 0 || (s % 2) == 1;
                        CursorShape = s <= 2 ? 0 : s <= 4 ? 1 : 2;
                    }
                    break;
            }
            Version++;
        }

        private int RowFloor() => _originMode ? _top : 0;
        private int RowCeil()  => _originMode ? _bottom : Rows - 1;
        private static int Clamp(int v, int n) => v < 0 ? 0 : v >= n ? n - 1 : v;

        private void PrivateMode(int[] p, bool set)
        {
            foreach (int m in p)
            {
                switch (m)
                {
                    case 1:    AppCursorKeys = set; break;
                    case 6:    _originMode = set; CursorRow = set ? _top : 0; CursorCol = 0; break;
                    case 7:    _autoWrap = set; break;
                    case 12:   CursorBlink = set; break;
                    case 25:   CursorVisible = set; break;
                    case 2004: BracketedPaste = set; break;

                    // The alternate screen: a full-screen app gets a clean grid that never
                    // touches scrollback, so quitting it puts your history back untouched.
                    case 47:
                    case 1047:
                    case 1049:
                        if (set && _altScreen == null)
                        {
                            if (m == 1049) SaveCursor();
                            _altScreen = _screen;
                            _screen = New(Cols, Rows);
                            CursorRow = CursorCol = 0;
                        }
                        else if (!set && _altScreen != null)
                        {
                            _screen = _altScreen;
                            _altScreen = null;
                            if (m == 1049) RestoreCursor();
                        }
                        break;
                }
            }
        }

        // ── Erase and edit ───────────────────────────────────────
        private void EraseDisplay(int mode)
        {
            _pendingWrap = false;
            switch (mode)
            {
                case 0:
                    ClearRun(_screen[CursorRow], CursorCol, Cols - 1);
                    for (int r = CursorRow + 1; r < Rows; r++) ClearRun(_screen[r], 0, Cols - 1);
                    break;
                case 1:
                    for (int r = 0; r < CursorRow; r++) ClearRun(_screen[r], 0, Cols - 1);
                    ClearRun(_screen[CursorRow], 0, CursorCol);
                    break;
                default:
                    for (int r = 0; r < Rows; r++) ClearRun(_screen[r], 0, Cols - 1);
                    // ED 3 clears scrollback too; that is what "clear" is bound to.
                    if (mode == 3) _scrollback.Clear();
                    break;
            }
            Version++;
        }

        private void EraseLine(int mode)
        {
            _pendingWrap = false;
            var line = _screen[CursorRow];
            if (mode == 0) ClearRun(line, CursorCol, Cols - 1);
            else if (mode == 1) ClearRun(line, 0, CursorCol);
            else ClearRun(line, 0, Cols - 1);
            Version++;
        }

        private void ClearRun(Cell[] line, int from, int to)
        {
            from = Math.Max(0, from);
            to = Math.Min(to, line.Length - 1);
            if (from > to) return;
            if (line[from].IsContinuation) from--;
            if (line[to].Width == 2) to++;
            ClearCells(line, from, to);
        }

        private void ClearCells(Cell[] line, int from, int to)
        {
            for (int c = Math.Max(0, from); c <= Math.Min(to, line.Length - 1); c++)
                line[c] = new Cell { Fg = DefaultColor, Bg = _curBg };
        }

        private void NormalizeLine(Cell[] line)
        {
            for (int c = 0; c < line.Length; c++)
            {
                bool orphan = line[c].IsContinuation && (c == 0 || line[c - 1].Width != 2);
                bool clipped = line[c].Width == 2 && (c + 1 >= line.Length || !line[c + 1].IsContinuation);
                if (orphan || clipped) ClearCells(line, c, c);
            }
        }

        private void InsertLines(int n)
        {
            if (CursorRow < _top || CursorRow > _bottom) return;
            n = Math.Min(n, _bottom - CursorRow + 1);
            for (int i = 0; i < n; i++)
            {
                var gone = _screen[_bottom];
                for (int r = _bottom; r > CursorRow; r--) _screen[r] = _screen[r - 1];
                _screen[CursorRow] = Blank(gone);
            }
            CursorCol = 0;
            Version++;
        }

        private void DeleteLines(int n)
        {
            if (CursorRow < _top || CursorRow > _bottom) return;
            n = Math.Min(n, _bottom - CursorRow + 1);
            for (int i = 0; i < n; i++)
            {
                var gone = _screen[CursorRow];
                for (int r = CursorRow; r < _bottom; r++) _screen[r] = _screen[r + 1];
                _screen[_bottom] = Blank(gone);
            }
            CursorCol = 0;
            Version++;
        }

        private void InsertChars(int n)
        {
            var line = _screen[CursorRow];
            n = Math.Min(n, Cols - CursorCol);
            if (n <= 0) return;
            if (line[CursorCol].IsContinuation) ClearRun(line, CursorCol, CursorCol);
            Array.Copy(line, CursorCol, line, CursorCol + n, Cols - CursorCol - n);
            ClearCells(line, CursorCol, CursorCol + n - 1);
            NormalizeLine(line);
            Version++;
        }

        private void DeleteChars(int n)
        {
            var line = _screen[CursorRow];
            n = Math.Min(n, Cols - CursorCol);
            if (n <= 0) return;
            ClearRun(line, CursorCol, CursorCol + n - 1);
            Array.Copy(line, CursorCol + n, line, CursorCol, Cols - CursorCol - n);
            ClearCells(line, Cols - n, Cols - 1);
            NormalizeLine(line);
            Version++;
        }

        private void EraseChars(int n)
        {
            ClearRun(_screen[CursorRow], CursorCol, CursorCol + Math.Max(1, n) - 1);
            Version++;
        }

        // ═══════════════════════════════════════════════════════════
        //  SGR
        // ═══════════════════════════════════════════════════════════
        private void Sgr(int[] p)
        {
            if (p.Length == 0) { _curFg = _curBg = DefaultColor; _curFlags = CellFlags.None; return; }

            for (int i = 0; i < p.Length; i++)
            {
                int v = p[i];
                switch (v)
                {
                    case 0: _curFg = _curBg = DefaultColor; _curFlags = CellFlags.None; break;
                    case 1: _curFlags |= CellFlags.Bold; break;
                    case 2: _curFlags |= CellFlags.Faint; break;
                    case 3: _curFlags |= CellFlags.Italic; break;
                    case 4: _curFlags |= CellFlags.Underline; break;
                    case 5: case 6: _curFlags |= CellFlags.Blink; break;
                    case 7: _curFlags |= CellFlags.Inverse; break;
                    case 8: _curFlags |= CellFlags.Hidden; break;
                    case 9: _curFlags |= CellFlags.Strike; break;
                    case 21: case 22: _curFlags &= ~(CellFlags.Bold | CellFlags.Faint); break;
                    case 23: _curFlags &= ~CellFlags.Italic; break;
                    case 24: _curFlags &= ~CellFlags.Underline; break;
                    case 25: _curFlags &= ~CellFlags.Blink; break;
                    case 27: _curFlags &= ~CellFlags.Inverse; break;
                    case 28: _curFlags &= ~CellFlags.Hidden; break;
                    case 29: _curFlags &= ~CellFlags.Strike; break;

                    case 39: _curFg = DefaultColor; break;
                    case 49: _curBg = DefaultColor; break;

                    case 38: i = ExtendedColor(p, i, ref _curFg); break;
                    case 48: i = ExtendedColor(p, i, ref _curBg); break;

                    default:
                        if (v >= 30 && v <= 37) _curFg = v - 30;
                        else if (v >= 40 && v <= 47) _curBg = v - 40;
                        else if (v >= 90 && v <= 97) _curFg = v - 90 + 8;      // bright
                        else if (v >= 100 && v <= 107) _curBg = v - 100 + 8;
                        break;
                }
            }
        }

        /// <summary>
        /// 38/48 take their argument inline: ";5;n" for a palette index, ";2;r;g;b" for
        /// truecolor. Returns the index of the last parameter consumed.
        /// </summary>
        private static int ExtendedColor(int[] p, int i, ref int slot)
        {
            if (i + 1 >= p.Length) return i;
            int kind = p[i + 1];
            if (kind == 5 && i + 2 < p.Length)
            {
                slot = p[i + 2] & 0xFF;
                return i + 2;
            }
            if (kind == 2 && i + 4 < p.Length)
            {
                slot = Rgb(p[i + 2] & 0xFF, p[i + 3] & 0xFF, p[i + 4] & 0xFF);
                return i + 4;
            }
            return i + 1;
        }

        // ═══════════════════════════════════════════════════════════
        //  OSC
        // ═══════════════════════════════════════════════════════════
        public void OscDispatch(int cmd, string data)
        {
            switch (cmd)
            {
                case 0: case 2:
                    Title = data;
                    TitleChanged?.Invoke(data);
                    break;

                // OSC 7 carries a file:// URL; OSC 9;9 carries a plain path, which is what
                // Windows Terminal's shell integration emits. Either lets the tab follow a cd.
                case 7:
                    SetDirFromUri(data);
                    break;
                case 9:
                    // Only the "9;" prefix comes off. Spaces are legal at either end of a Linux
                    // folder name, so trimming would point the tab at a different folder.
                    if (data.StartsWith("9;", StringComparison.Ordinal))
                        SetDir(Unquote(data[2..]));
                    break;
            }
        }

        /// <summary>
        /// Windows Terminal's documented prompt wraps the path in double quotes. A real path
        /// never starts and ends with one (Windows forbids them; a Linux path starts with '/'),
        /// so a quoted pair is the wrapper and comes off. Anything else is kept exactly.
        /// </summary>
        private static string Unquote(string path)
            => path.Length >= 2 && path[0] == '"' && path[path.Length - 1] == '"' ? path[1..^1] : path;

        private void SetDirFromUri(string data)
        {
            try
            {
                var u = new Uri(data);
                if (u.IsFile) SetDir(u.LocalPath);
            }
            catch { /* a malformed URL is not worth reporting */ }
        }

        private void SetDir(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == WorkingDirectory) return;
            if (IsRemotePath(path)) return;
            WorkingDirectory = path;
            DirectoryChanged?.Invoke(path);
        }

        /// <summary>
        /// A network share reported by terminal output. Any program can print these sequences,
        /// including a file shown with type or cat, so a report naming \\some-server\share
        /// would make the tab's folder and its folder button point at a machine the user never
        /// chose, and opening it hands that machine the user's sign-in hash. Local folders and
        /// WSL's own \\wsl$ and \\wsl.localhost paths are kept; a real cd into a share leaves
        /// the tab on the last local folder.
        /// </summary>
        internal static bool IsRemotePath(string path)
        {
            string p = path.Replace('/', '\\');
            if (!p.StartsWith(@"\\", StringComparison.Ordinal)) return false;

            string rest = p.Substring(2);
            if (rest.StartsWith(@"?\UNC\", StringComparison.OrdinalIgnoreCase)) rest = rest.Substring(6);
            else if (rest.StartsWith(@"?\", StringComparison.Ordinal) || rest.StartsWith(@".\", StringComparison.Ordinal))
                return !(rest.Length > 3 && rest[3] == ':');   // \\?\C:\ is local; device paths are not folders

            int slash = rest.IndexOf('\\');
            string host = slash < 0 ? rest : rest.Substring(0, slash);
            return !(host.Equals("wsl$", StringComparison.OrdinalIgnoreCase)
                     || host.Equals("wsl.localhost", StringComparison.OrdinalIgnoreCase)
                     || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                     || host == "127.0.0.1");
        }
    }
}
