using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

// The terminal surface: draws the buffer, encodes keystrokes, owns the pty.
//
// Rendering is done by hand rather than with TextBlocks because a terminal is a GRID. Laying
// out 80x25 TextBlocks and letting WPF measure them would be slow and, worse, would let the
// font's own kerning drift columns out of alignment - box drawing and progress bars need
// every cell to land on an exact multiple of the advance width. Drawing GlyphRuns at computed
// origins guarantees that.
//
// Threading: the pipe is read on a background thread, which only ever enqueues bytes. A
// dispatcher timer drains that queue and does the parsing on the UI thread, so the buffer
// needs no lock. The timer also coalesces: dumping a large file arrives as hundreds of reads
// and repaints at most once per tick rather than once per read.
using KillerShell.Shell;

namespace KillerShell.Terminal
{
    internal sealed partial class TerminalControl : FrameworkElement
    {
        // Control characters are built from CODEPOINTS, the same convention the MDL2 glyphs
        // elsewhere in this project use. A literal escape byte in a string is invisible in an
        // editor, cannot be grepped, and does not survive an encoding round trip.
        private static readonly string Esc = ((char)0x1B).ToString();
        private static readonly string Csi = Esc + "[";
        private static readonly string Bs  = ((char)0x08).ToString();
        private static readonly string Del = ((char)0x7F).ToString();
        private static readonly string Nul = ((char)0x00).ToString();

        private readonly TerminalBuffer _buf;
        private readonly VtParser _parser;
        private ConPtySession? _pty;
        private TerminalPalette _palette;

        private readonly Queue<byte[]> _incoming = new();
        private readonly object _gate = new();
        private DispatcherTimer? _pump;
        private int _drawnVersion = -1;

        private GlyphTypeface? _glyphs;
        private double _cellW, _cellH, _baseline;
        private double _fontSize = 12;   // overwritten from the setting in the ctor
        private float _pixelsPerDip = 1f;

        private int _scroll;                 // lines scrolled back; 0 is live
        private bool _cursorOn = true;
        private DispatcherTimer? _blink;

        /// <summary>--demo's canned session, waiting for the control to be given a real size.</summary>
        private string? _demoPending;

        /// <summary>Raised when the child exits, so the tab can close or show a notice.</summary>
        public event Action<int>? Exited;

        public TerminalBuffer Buffer => _buf;

        public TerminalControl(TerminalSkin skin)
        {
            _palette = TerminalPalette.For(skin);
            _buf = new TerminalBuffer(80, 25);
            _parser = new VtParser(_buf);

            Focusable = true;
            FocusVisualStyle = null;
            ClipToBounds = true;

            // Ideal, not Display. Display mode rounds advance widths to whole pixels, and a
            // rounded advance multiplied by 200 columns walks the grid out of alignment, which
            // comes apart worst on box drawing. The cost is very slightly softer glyphs.
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);

            // The buffer emits COMPLETE replies, escape included, so they pass straight
            // through. Prefixing another escape here would double it.
            _buf.Respond += Send;

            // Before LoadFont, since the cell metrics are derived from the size.
            _fontSize = MainWindow.TerminalFontSize;

            LoadFont();
            Loaded += (_, _) => Focus();
        }

        /// <summary>
        /// Pixel width <paramref name="cols"/> columns need at the current font. Zero until the
        /// typeface has resolved, so callers must treat 0 as "not ready".
        /// </summary>
        public double WidthForColumns(int cols) => cols * _cellW;

        public void SetFontSize(double size)
        {
            _fontSize = Math.Max(8, Math.Min(28, size));
            LoadFont();
            ApplySize();
            InvalidateVisual();

            // Readout in the window's status line - "Text Size: 110%" - the same feedback the
            // app-wide zoom gives (Steve, 2026-08-09). Best-effort: a terminal being resized
            // before the window exists just skips it.
            (System.Windows.Application.Current?.MainWindow as KillerShell.Shell.MainWindow)
                ?.ShowTerminalTextSize(_fontSize);
        }

        public void SetSkin(TerminalSkin skin)
        {
            _palette = TerminalPalette.For(skin);
            InvalidateVisual();
        }

        /// <summary>Rebuild theme-derived colors. Called on a theme or accent switch.</summary>
        public void RefreshTheme()
        {
            _palette = TerminalPalette.For(_palette.Skin);
            InvalidateVisual();
        }

        // ═══════════════════════════════════════════════════════════
        //  FONT
        // ═══════════════════════════════════════════════════════════
        // Cascadia first because it is the console face Windows ships now and it carries the
        // box drawing and powerline glyphs a modern prompt uses; Consolas is the guaranteed
        // fallback. All monospace, so one advance width describes the whole grid.
        private static readonly string[] FontOrder =
        [
            "Cascadia Mono", "Cascadia Code", "Consolas", "Lucida Console", "Courier New",
        ];

        // ── The fallback face ────────────────────────────────────
        // NO stock Windows monospaced font has the powerline separators or the git branch mark,
        // and Consolas does not even have the prompt's chevron - so on a machine that has never
        // had a Nerd Font installed, the shipped prompt came out as a row of boxes. Bundling a
        // whole Nerd Font to fix that costs 2.6 MB, 99% of which is icons nobody's prompt draws.
        //
        // So the exe carries twenty-six glyphs instead (Fonts/KillerGlyphs.ttf, 2.9 KB) and a
        // cell is drawn from them only when the CHOSEN face has nothing for that codepoint. The
        // user's font choice is untouched; it just stops being able to fail on those glyphs.
        private static GlyphTypeface? _fallbackFace;
        private static bool _fallbackTried;

        private GlyphTypeface? _fallback;

        /// <summary>Horizontal stretch that makes a fallback glyph fill the chosen font's cell.</summary>
        /// <remarks>
        /// The two faces have different advance widths - the fallback is half an em, Consolas is
        /// nearer 0.55 - and a powerline separator is a solid triangle that has to butt against
        /// the next cell. Left unscaled it leaves a hairline gap exactly where the eye is drawn.
        /// </remarks>
        private double _fallbackScale = 1;

        /// <summary>Why the fallback did not load, empty when it did. Surfaced by KS_GLYPHS.</summary>
        internal static string FallbackStatus { get; private set; } = string.Empty;

        /// <summary>
        /// The bundled face, resolved once per process.
        /// </summary>
        /// <remarks>
        /// THREE URI forms, tried in order, because there is no way to test this from a headless
        /// session - WPF's font cache refuses to initialise outside an interactive one, so every
        /// form throws there whether it is right or wrong. Rather than ship one guess, all three
        /// spellings that reach a Resource in this assembly are tried:
        ///
        ///   1. GlyphTypeface straight off the pack URI, assembly named. No family-name lookup
        ///      at all, so a mismatch between the font's name table and the string here cannot
        ///      break it.
        ///   2. FontFamily against the assembly's component root - the code equivalent of the
        ///      wordmark font's XAML form in Controls.xaml.
        ///   3. FontFamily against the application root, which is what most examples show but
        ///      which resolves against the ENTRY assembly and so is the most fragile of the three.
        ///
        /// The first that yields a face with the powerline separator in it wins.
        /// </remarks>
        private static GlyphTypeface? Fallback()
        {
            if (_fallbackTried) return _fallbackFace;
            _fallbackTried = true;

            var errors = new List<string>();

            try
            {
                var gt = new GlyphTypeface(
                    new Uri("pack://application:,,,/KillerShell;component/Fonts/KillerGlyphs.ttf"));
                if (gt.CharacterToGlyphMap.ContainsKey(0xE0B0)) { _fallbackFace = gt; return gt; }
                errors.Add("direct: loaded but no E0B0");
            }
            catch (Exception ex) { errors.Add("direct: " + ex.Message); }

            foreach (var root in new[] { "pack://application:,,,/KillerShell;component/",
                                         "pack://application:,,,/" })
            {
                try
                {
                    var fam = new FontFamily(new Uri(root), "./Fonts/#KillerGlyphs");
                    var tf = new Typeface(fam, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                    if (tf.TryGetGlyphTypeface(out var gt)
                        && gt.CharacterToGlyphMap.ContainsKey(0xE0B0))
                    {
                        _fallbackFace = gt;
                        return gt;
                    }
                    errors.Add(root + ": no face");
                }
                catch (Exception ex) { errors.Add(root + ": " + ex.Message); }
            }

            // Swallowed as far as the UI goes - a missing fallback must never stop a shell
            // opening - but recorded, because "the glyphs are boxes" is otherwise indis-
            // tinguishable from "this font has no glyphs", which is the whole thing this
            // feature exists to tell apart.
            FallbackStatus = string.Join(" | ", errors);
            return null;
        }

        /// <summary>
        /// Re-resolve the typeface and repaint. Called when the font dialog changes the terminal
        /// slot, so an open shell picks the new face up live rather than on the next tab.
        /// </summary>
        public void ReloadFont()
        {
            LoadFont();
            ApplySize();
            InvalidateVisual();
        }

        private void LoadFont()
        {
            // Tried in order: the user's pick from the font dialog, then the app's preferred
            // face if this machine has it (Fonts.cs DefaultMonoFont - empty when it does not),
            // then the chain below. Each candidate still has to pass the glyph checks, so a
            // proportional or broken face falls through rather than shearing the grid.
            var head = new List<string>(2);
            if (!string.IsNullOrEmpty(MainWindow.TerminalFontFamily)) head.Add(MainWindow.TerminalFontFamily);
            if (!string.IsNullOrEmpty(MainWindow.DefaultMonoFont))    head.Add(MainWindow.DefaultMonoFont);

            string[] order;
            if (head.Count == 0) order = FontOrder;
            else
            {
                order = new string[head.Count + FontOrder.Length];
                head.CopyTo(order, 0);
                FontOrder.CopyTo(order, head.Count);
            }

            foreach (var name in order)
            {
                try
                {
                    var tf = new Typeface(new FontFamily(name), FontStyles.Normal,
                                          FontWeights.Normal, FontStretches.Normal);
                    if (!tf.TryGetGlyphTypeface(out var gt)) continue;
                    if (!gt.CharacterToGlyphMap.ContainsKey('M')) continue;

                    _glyphs = gt;
                    _cellW = Math.Round(gt.AdvanceWidths[gt.CharacterToGlyphMap['M']] * _fontSize, 2);
                    _cellH = Math.Ceiling(gt.Height * _fontSize);
                    _baseline = Math.Round(gt.Baseline * _fontSize, 2);

                    // Measured against the fallback's OWN advance rather than assumed to be half
                    // an em, so re-subsetting the font from a different source cannot quietly put
                    // every powerline separator a few pixels short.
                    _fallback = Fallback();
                    _fallbackScale = 1;
                    if (_fallback != null
                        && _fallback.CharacterToGlyphMap.TryGetValue(0xE0B0, out ushort probe))
                    {
                        double own = _fallback.AdvanceWidths[probe] * _fontSize;
                        if (own > 0.01) _fallbackScale = Math.Round(_cellW / own, 4);
                    }
                    return;
                }
                catch { /* try the next face */ }
            }
        }

        // ═══════════════════════════════════════════════════════════
        //  SESSION
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// A row of the bundled glyphs plus a verdict, printed into the buffer.
        /// </summary>
        /// <remarks>
        /// Set KS_GLYPHS=1 to get it on every shell. It exists because the fallback cannot be
        /// tested anywhere except in a running window - WPF will not initialise a font cache in
        /// a headless session - and "those are boxes" otherwise gives no way to tell a fallback
        /// that failed to load from a font that simply has no glyphs.
        /// </remarks>
        private void GlyphSelfTest()
        {
            if (Environment.GetEnvironmentVariable("KS_GLYPHS") != "1") return;

            string sample = string.Concat(
                ((char)0xE0B0).ToString(), " ", ((char)0xE0A0).ToString(), " ",
                ((char)0x276F).ToString(), " ", ((char)0x2191).ToString(),
                ((char)0x2193).ToString(), " ", ((char)0x00B1).ToString());

            string verdict = _fallback != null
                ? "fallback loaded, scale " + _fallbackScale.ToString("0.###",
                      System.Globalization.CultureInfo.InvariantCulture)
                : "FALLBACK NOT LOADED - " + (FallbackStatus.Length > 0 ? FallbackStatus : "unknown");

            WriteLocal("\r\n  glyph check  " + sample + "   (" + verdict + ")\r\n"
                     + "  any box above is a codepoint neither your font nor the bundled one has\r\n\r\n");
        }

        public void Start(string commandLine, string workingDir)
        {
            ApplySize();
            GlyphSelfTest();

            try
            {
                _pty = ConPtySession.Start(commandLine, workingDir, (short)_buf.Cols, (short)_buf.Rows);
            }
            catch (Exception ex)
            {
                // Printed INTO the buffer rather than thrown: the tab is already open, and a
                // message where the shell would have been is more use than a dialog.
                WriteLocal("\r\n  Could not start the shell.\r\n  " + ex.Message + "\r\n");
                return;
            }

            _pty.Exited += code => Dispatcher.BeginInvoke(new Action(() =>
            {
                WriteLocal("\r\n[process exited with code " + code + "]\r\n");
                Exited?.Invoke(code);
            }));

            var stream = _pty.Output;
            var reader = new Thread(() =>
            {
                var buf = new byte[8192];
                try
                {
                    int n;
                    while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                    {
                        var chunk = new byte[n];
                        Array.Copy(buf, chunk, n);
                        lock (_gate) _incoming.Enqueue(chunk);
                    }
                }
                catch { /* the pipe closing is the normal way out */ }
            })
            { IsBackground = true, Name = "ConPTY read" };
            reader.Start();

            _pump = new DispatcherTimer(DispatcherPriority.Render)
            { Interval = TimeSpan.FromMilliseconds(16) };
            _pump.Tick += (_, _) => Drain();
            _pump.Start();

            _blink = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
            _blink.Tick += (_, _) =>
            {
                if (!_buf.CursorBlink) { _cursorOn = true; return; }
                _cursorOn = !_cursorOn;
                InvalidateVisual();
            };
            _blink.Start();
        }

        /// <summary>
        /// Show a canned session with nothing behind it. What --demo opens instead of Start.
        /// </summary>
        /// <remarks>
        /// No pseudoconsole is created AT ALL, which is the point: a capture can never end up
        /// with a real shell running inside it, and nothing typed at the tab can reach a machine
        /// - Send already no-ops without a pty. There is no reader thread and no pump timer
        /// either, because nothing will ever arrive to drain. The glyph self-test is skipped as
        /// well: it is a diagnostic banner, and here it would be the first thing in the shot.
        ///
        /// The text goes through the same local-write path the self-test and the shell-failure
        /// notice use, so it is parsed as VT and comes out colored, and the buffer's version
        /// bump is what gets it drawn. The blink timer is started exactly as Start does it, so
        /// the demo shell has a live cursor sitting at its prompt.
        /// </remarks>
        public void StartDemo(string canned)
        {
            // Held rather than written here. StartDemo runs while the tab is still being built,
            // which is before WPF has measured this control: ActualWidth is 0, so ApplySize
            // makes the buffer one column by one row, and feeding it a full session scrolls the
            // whole thing into scrollback and draws an empty terminal. A real shell never hits
            // this because its output arrives on the pump AFTER layout. ApplySize flushes this
            // as soon as the control has a genuine size.
            _demoPending = canned;
            ApplySize();

            _blink = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
            _blink.Tick += (_, _) =>
            {
                if (!_buf.CursorBlink) { _cursorOn = true; return; }
                _cursorOn = !_cursorOn;
                InvalidateVisual();
            };
            _blink.Start();
        }

        private void Drain()
        {
            bool any = false;
            while (true)
            {
                byte[] chunk;
                lock (_gate)
                {
                    if (_incoming.Count == 0) break;
                    chunk = _incoming.Dequeue();
                }
                _parser.Feed(chunk, chunk.Length);
                any = true;
            }
            if (any) _scroll = 0;               // new output jumps back to the bottom
            if (_buf.Version != _drawnVersion) InvalidateVisual();
        }

        /// <summary>Feed text to our own parser without sending it to the shell.</summary>
        private void WriteLocal(string s)
        {
            var b = Encoding.UTF8.GetBytes(s);
            _parser.Feed(b, b.Length);
            InvalidateVisual();
        }

        public void Send(string s)
        {
            if (_pty == null || _pty.HasExited || string.IsNullOrEmpty(s)) return;
            try
            {
                var b = Encoding.UTF8.GetBytes(s);
                _pty.Input.Write(b, 0, b.Length);
                _pty.Input.Flush();
            }
            catch { /* the shell went away between keystroke and write */ }
        }

        public void Close()
        {
            _pump?.Stop();
            _blink?.Stop();
            _pty?.Dispose();
            _pty = null;
        }

        // ═══════════════════════════════════════════════════════════
        //  SIZE
        // ═══════════════════════════════════════════════════════════
        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            _pixelsPerDip = (float)VisualTreeHelper.GetDpi(this).PixelsPerDip;
            ApplySize();
        }

        private void ApplySize()
        {
            if (_cellW <= 0 || _cellH <= 0) return;
            int cols = Math.Max(1, (int)(ActualWidth / _cellW));
            int rows = Math.Max(1, (int)(ActualHeight / _cellH));

            if (cols != _buf.Cols || rows != _buf.Rows)
            {
                _buf.Resize(cols, rows);
                _pty?.Resize((short)cols, (short)rows);
                InvalidateVisual();
            }

            FlushDemo(cols, rows);
        }

        /// <summary>
        /// Writes --demo's canned session once the control is actually big enough to hold it.
        /// </summary>
        /// <remarks>
        /// The size test is not paranoia. Before WPF measures this control ActualWidth is 0, so
        /// the arithmetic above clamps to a 1x1 buffer, and a session written into that is gone
        /// - every line scrolls into scrollback a single column wide. Waiting for a plausible
        /// terminal shape means the first frame a reader ever sees is the finished session.
        /// </remarks>
        private void FlushDemo(int cols, int rows)
        {
            if (_demoPending == null || cols < 20 || rows < 5) return;

            string canned = _demoPending;
            _demoPending = null;      // cleared first: WriteLocal can re-enter through layout
            WriteLocal(canned);
        }

        // ═══════════════════════════════════════════════════════════
        //  RENDER
        // ═══════════════════════════════════════════════════════════
        protected override void OnRender(DrawingContext dc)
        {
            _drawnVersion = _buf.Version;

            var rect = new Rect(0, 0, ActualWidth, ActualHeight);
            var bg = new SolidColorBrush(_palette.Background);
            bg.Freeze();
            dc.DrawRectangle(bg, null, rect);

            // Grain sits on the background only, the same as every other pane surface in the
            // app - drawn here rather than as a separate Border, because this control fills its
            // whole rect opaquely itself (the line above), so nothing behind it could ever show
            // through. Glyphs are drawn after this and land on top, so the texture never covers
            // the writing (Steve, 2026-08-03 - a prior fix removed grain from here entirely
            // instead of moving it into the paint, which left the terminal with no texture at
            // all rather than texture only where there is no text).
            if (TryFindResource("GrainTileBrush") is Brush grain)
            {
                double opacity = TryFindResource("GrainOpacity") is double d ? d : 0.2;
                dc.PushOpacity(opacity);
                dc.DrawRectangle(grain, null, rect);
                dc.Pop();
            }

            if (_glyphs == null) return;

            int first = Math.Max(0, _buf.ScrollbackCount - _scroll);

            for (int r = 0; r < _buf.Rows; r++)
            {
                var line = _buf.LineAt(first + r);
                double y = r * _cellH;
                DrawBackgrounds(dc, line, y);
                DrawGlyphs(dc, line, y);
            }

            DrawSelection(dc, first);   // TerminalSelection.cs
            DrawCursor(dc);

            if (_palette.Scanlines) DrawScanlines(dc);
        }

        private void DrawBackgrounds(DrawingContext dc, Cell[] line, double y)
        {
            int c = 0;
            while (c < line.Length)
            {
                var color = CellBg(line[c]);
                int start = c;
                while (c < line.Length && CellBg(line[c]) == color) c++;
                if (color != _palette.Background)
                {
                    var b = new SolidColorBrush(color);
                    b.Freeze();
                    dc.DrawRectangle(b, null,
                        new Rect(start * _cellW, y, (c - start) * _cellW, _cellH));
                }
            }
        }

        private Color CellBg(Cell cell)
        {
            bool inv = (cell.Flags & CellFlags.Inverse) != 0;
            return _palette.Resolve(inv ? cell.Fg : cell.Bg, !inv);
        }

        private Color CellFg(Cell cell)
        {
            if ((cell.Flags & CellFlags.Hidden) != 0) return CellBg(cell);
            bool inv = (cell.Flags & CellFlags.Inverse) != 0;
            var fg = _palette.Resolve(inv ? cell.Bg : cell.Fg, inv);

            // Corrected against THIS cell's background, not against the pane: a cell with an
            // explicit SGR background is its own little surface, and blue-on-blue inside a
            // highlighted run is just as unreadable as blue-on-blue over the theme.
            return _palette.Readable(fg, CellBg(cell));
        }

        private void DrawGlyphs(DrawingContext dc, Cell[] line, double y)
        {
            var gt = _glyphs!;
            int c = 0;
            while (c < line.Length)
            {
                if (line[c].Ch == 0 || line[c].Ch == ' ') { c++; continue; }

                var fg = CellFg(line[c]);
                var flags = line[c].Flags;
                int start = c;

                // Which face this run is drawn from. A GlyphRun carries exactly ONE typeface, so
                // a cell that has to come from the bundled fallback breaks the run.
                var face = FaceFor(gt, line[c].Ch);
                bool viaFallback = !ReferenceEquals(face, gt);

                var indices = new List<ushort>();
                var widths = new List<double>();

                while (c < line.Length && line[c].Ch != 0 && line[c].Ch != ' '
                       && CellFg(line[c]) == fg && line[c].Flags == flags
                       && ReferenceEquals(FaceFor(gt, line[c].Ch), face)
                       // A fallback cell is taken ONE at a time: it is drawn inside a horizontal
                       // scale anchored at the run's origin, and that transform would misplace
                       // every cell after the first.
                       && (indices.Count == 0 || !viaFallback))
                {
                    // A codepoint neither face has becomes a box rather than vanishing silently,
                    // which is how you notice a missing font.
                    int cp = line[c].Ch;
                    if (!face.CharacterToGlyphMap.TryGetValue(cp, out ushort gi))
                        face.CharacterToGlyphMap.TryGetValue(0x25A1, out gi);

                    indices.Add(gi);
                    widths.Add(_cellW);
                    c++;
                }

                if (indices.Count == 0) continue;

                double x = start * _cellW;
                var brush = new SolidColorBrush(
                    (flags & CellFlags.Faint) != 0
                        ? Color.FromArgb(0x99, fg.R, fg.G, fg.B)
                        : fg);
                brush.Freeze();

                // Stretch a fallback glyph out to the chosen font's cell width, anchored at this
                // cell's own left edge so nothing downstream of it moves.
                bool stretch = viaFallback && Math.Abs(_fallbackScale - 1) > 0.005;
                if (stretch) dc.PushTransform(new ScaleTransform(_fallbackScale, 1, x, 0));

                // Phosphor bleed: the same run again, translucent and a hair low, UNDER the
                // crisp one. Not a blur effect - a bitmap effect over text destroys ClearType,
                // which is the family rule, and an offset copy reads as bleed anyway.
                if (_palette.Glow > 0)
                {
                    var glow = new SolidColorBrush(
                        Color.FromArgb((byte)(70 * _palette.Glow), fg.R, fg.G, fg.B));
                    glow.Freeze();
                    var under = MakeRun(face, indices, widths, new Point(x, y + _baseline + 0.7));
                    if (under != null) dc.DrawGlyphRun(glow, under);
                }

                var run = MakeRun(face, indices, widths, new Point(x, y + _baseline));
                if (run != null) dc.DrawGlyphRun(brush, run);

                if (stretch) dc.Pop();

                double w = (c - start) * _cellW;
                if ((flags & CellFlags.Underline) != 0)
                    dc.DrawRectangle(brush, null, new Rect(x, y + _baseline + 1.5, w, 1));
                if ((flags & CellFlags.Strike) != 0)
                    dc.DrawRectangle(brush, null, new Rect(x, y + _baseline * 0.65, w, 1));
            }
        }

        /// <summary>
        /// The face <paramref name="cp"/> is drawn from: the chosen one, or the bundled fallback
        /// when the chosen one has no glyph for it.
        /// </summary>
        /// <remarks>
        /// Returns the primary for a codepoint NEITHER face has, so the box-drawing substitution
        /// in the caller happens against the font the rest of the line is in.
        /// </remarks>
        private GlyphTypeface FaceFor(GlyphTypeface primary, int cp)
        {
            if (primary.CharacterToGlyphMap.ContainsKey(cp)) return primary;
            if (_fallback != null && _fallback.CharacterToGlyphMap.ContainsKey(cp)) return _fallback;
            return primary;
        }

        /// <summary>
        /// The pixelsPerDip overload on purpose: the older constructor is obsolete from 4.6.2
        /// on, and this project builds warning free.
        /// </summary>
        private GlyphRun? MakeRun(GlyphTypeface gt, IList<ushort> indices, IList<double> widths, Point origin)
        {
            try
            {
                return new GlyphRun(gt, 0, false, _fontSize, _pixelsPerDip, indices, origin,
                                    widths, null, null, null, null, null, null);
            }
            catch { return null; }   // a face that refuses a run must not take the pane down
        }

        private void DrawCursor(DrawingContext dc)
        {
            if (!_buf.CursorVisible || !_cursorOn || _scroll != 0 || !IsKeyboardFocusWithin) return;

            double x = _buf.CursorCol * _cellW;
            double y = _buf.CursorRow * _cellH;
            var b = new SolidColorBrush(_palette.Cursor);
            b.Freeze();

            switch (_buf.CursorShape)
            {
                case 1: dc.DrawRectangle(b, null, new Rect(x, y + _cellH - 2, _cellW, 2)); break;
                case 2: dc.DrawRectangle(b, null, new Rect(x, y, 2, _cellH)); break;
                default:
                    dc.DrawRectangle(b, null, new Rect(x, y, _cellW, _cellH));

                    // Repaint the glyph underneath in the background color, so a block cursor
                    // does not hide the character it is sitting on.
                    var cell = _buf.LineAt(_buf.ScrollbackCount + _buf.CursorRow)[_buf.CursorCol];
                    if (cell.Ch != 0 && cell.Ch != ' ' && _glyphs != null
                        && _glyphs.CharacterToGlyphMap.TryGetValue(cell.Ch, out ushort gi))
                    {
                        var hole = new SolidColorBrush(_palette.Background);
                        hole.Freeze();
                        var run = MakeRun(_glyphs, [gi], [_cellW], new Point(x, y + _baseline));
                        if (run != null) dc.DrawGlyphRun(hole, run);
                    }
                    break;
            }
        }

        // Every other device pixel, at low alpha. Drawn last so it lies OVER the glyphs, which
        // is what makes it read as a screen rather than as a texture behind the text.
        private void DrawScanlines(DrawingContext dc)
        {
            var b = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0));
            b.Freeze();
            for (double y = 0; y < ActualHeight; y += 2)
                dc.DrawRectangle(b, null, new Rect(0, y, ActualWidth, 1));
        }

        // ═══════════════════════════════════════════════════════════
        //  SCROLLBACK
        // ═══════════════════════════════════════════════════════════
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            // Ctrl+wheel resizes the text, as it does in Windows Terminal and every browser.
            // The status line shows the resulting percentage (AppScale.ShowTerminalTextSize).
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                SetFontSize(_fontSize + (e.Delta > 0 ? 1 : -1));
                e.Handled = true;
                return;
            }

            // The alternate screen has no scrollback of its own, so the wheel belongs to the
            // application running in it (less, vim) rather than to us.
            if (_buf.AltScreen)
            {
                string arrow = Csi + (e.Delta > 0 ? "A" : "B");
                Send(arrow + arrow + arrow);
                e.Handled = true;
                return;
            }

            int lines = Math.Max(1, SystemParameters.WheelScrollLines);
            _scroll = Math.Max(0, Math.Min(_buf.ScrollbackCount,
                                           _scroll + (e.Delta > 0 ? lines : -lines)));
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            Focus();

            // Middle-click pastes, the way it does in a terminal everywhere else.
            if (e.ChangedButton == MouseButton.Middle) { Paste(); e.Handled = true; return; }

            if (e.ChangedButton == MouseButton.Left && !_hasSelection) ClearSelection();
            SelectionMouseDown(e);           // TerminalSelection.cs
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            SelectionMouseMove(e);
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            SelectionMouseUp();
            base.OnMouseUp(e);
        }

        // ═══════════════════════════════════════════════════════════
        //  KEYBOARD
        // ═══════════════════════════════════════════════════════════
        protected override void OnTextInput(TextCompositionEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Text)) return;

            // Control characters arrive through OnPreviewKeyDown, where the modifier is still
            // visible; letting them through here as well would send everything twice.
            if (e.Text.Length == 1 && e.Text[0] < 0x20) return;

            Send(e.Text);
            _scroll = 0;
            e.Handled = true;
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            bool ctrl = (mods & ModifierKeys.Control) != 0;
            bool shift = (mods & ModifierKeys.Shift) != 0;
            bool alt = (mods & ModifierKeys.Alt) != 0;

            if (HandleTerminalChord(e.Key, ctrl, shift, alt)) { e.Handled = true; return; }

            // Ctrl+C copies WHEN THERE IS A SELECTION and interrupts otherwise, which is what
            // Windows Terminal does. Copy returning false is what makes the fall-through work,
            // so a runaway command is still killable with the chord everyone reaches for.
            if (ctrl && !shift && !alt && e.Key == Key.C && CopySelection())
            {
                ClearSelection();
                e.Handled = true;
                return;
            }

            string? seq = Encode(e.Key, ctrl, shift, alt);
            if (seq == null) return;

            // Typing dismisses a selection, the way it does in an editor.
            ClearSelection();
            Send(seq);
            _scroll = 0;
            e.Handled = true;
        }

        /// <summary>
        /// The TERMINAL's own chords, taken from Windows Terminal's defaults so a hand trained
        /// there arrives expecting them. Everything here acts on the view - clipboard, zoom,
        /// scrollback - and none of it is sent to the shell.
        /// </summary>
        /// <remarks>
        /// PSReadLine's bindings are deliberately NOT in this list. Ctrl+A, Ctrl+E, Ctrl+R,
        /// Alt+F, Ctrl+Left and the rest belong to the shell and are already handled by
        /// encoding them faithfully and getting out of the way. Implementing them here would
        /// mean reimplementing a line editor that is already running.
        /// </remarks>
        private bool HandleTerminalChord(Key key, bool ctrl, bool shift, bool alt)
        {
            if (alt) return false;

            if (ctrl && shift)
            {
                switch (key)
                {
                    case Key.C: CopySelection(); ClearSelection(); return true;
                    case Key.V: Paste(); return true;
                    case Key.A: SelectAll(); return true;

                    // Scroll the view without disturbing the command line.
                    case Key.Up:       ScrollBy(1); return true;
                    case Key.Down:     ScrollBy(-1); return true;
                    case Key.PageUp:   ScrollBy(_buf.Rows); return true;
                    case Key.PageDown: ScrollBy(-_buf.Rows); return true;
                    case Key.Home:     ScrollTo(_buf.ScrollbackCount); return true;
                    case Key.End:      ScrollTo(0); return true;
                }
                return false;
            }

            if (ctrl)
            {
                switch (key)
                {
                    // Both the main row and the numpad, because a keyboard has two of each and
                    // OemPlus is what the unshifted "+" key reports.
                    case Key.OemPlus: case Key.Add:       SetFontSize(_fontSize + 1); return true;
                    case Key.OemMinus: case Key.Subtract: SetFontSize(_fontSize - 1); return true;
                    case Key.D0: case Key.NumPad0:        SetFontSize(13); return true;

                    // Bare Ctrl+V pastes, same as Windows Terminal. Without this case it fell
                    // through to Encode()'s generic Ctrl+letter handling below, which sends the
                    // raw C0 control byte (SYN, 0x16) to the shell instead of the clipboard -
                    // a no-op in cmd.exe and unreliable in PowerShell. Ctrl+Shift+V and
                    // Shift+Insert above already do this; this just gives the unshifted chord
                    // the same treatment instead of leaving it as the only common paste
                    // shortcut that silently did nothing.
                    case Key.V: Paste(); return true;
                }
            }

            if (shift && key == Key.Insert) { Paste(); return true; }

            return false;
        }

        private void ScrollBy(int lines) => ScrollTo(_scroll + lines);

        private void ScrollTo(int lines)
        {
            _scroll = Math.Max(0, Math.Min(_buf.ScrollbackCount, lines));
            InvalidateVisual();
        }

        private string? Encode(Key key, bool ctrl, bool shift, bool alt)
        {
            // Arrows and Home/End change form when the shell asks for application mode, which
            // PSReadLine does. Getting this wrong is why arrow keys print gibberish. A MODIFIED
            // arrow is always CSI form regardless, with the modifier as a parameter.
            int mod = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);

            // TWO parameter forms, and mixing them up is the classic bug here. A cursor key
            // takes CSI 1;5D - the leading 1 is not optional, and CSI ;5D is simply malformed,
            // which is what breaks Ctrl+Left word navigation in PSReadLine. A tilde key takes
            // CSI 3;5~, where the number IS the key and the modifier follows it.
            string mArrow = mod > 1 ? "1;" + mod : "";
            string mTilde = mod > 1 ? ";" + mod : "";
            string pre = mod > 1 ? Csi : (_buf.AppCursorKeys ? Esc + "O" : Csi);

            switch (key)
            {
                case Key.Up:    return pre + mArrow + "A";
                case Key.Down:  return pre + mArrow + "B";
                case Key.Right: return pre + mArrow + "C";
                case Key.Left:  return pre + mArrow + "D";
                case Key.Home:  return pre + mArrow + "H";
                case Key.End:   return pre + mArrow + "F";

                case Key.Enter:  return alt ? Esc + "\r" : "\r";
                case Key.Tab:    return shift ? Csi + "Z" : "\t";
                case Key.Escape: return Esc;

                // BS for Backspace, DEL for Ctrl+Backspace. This is the WINDOWS mapping and it
                // is the opposite of the Unix convention: conpty turns the incoming byte into a
                // console key record, and 0x08 is what VK_BACK produces there, so a terminal
                // that sends DEL for a plain Backspace leaves you unable to delete anything.
                case Key.Back:   return ctrl ? Del : Bs;

                case Key.Delete:   return Csi + "3" + mTilde + "~";
                case Key.Insert:   return Csi + "2" + mTilde + "~";
                case Key.PageUp:   return Csi + "5" + mTilde + "~";
                case Key.PageDown: return Csi + "6" + mTilde + "~";

                case Key.F1:  return Esc + "OP";
                case Key.F2:  return Esc + "OQ";
                case Key.F3:  return Esc + "OR";
                case Key.F4:  return Esc + "OS";
                case Key.F5:  return Csi + "15~";
                case Key.F6:  return Csi + "17~";
                case Key.F7:  return Csi + "18~";
                case Key.F8:  return Csi + "19~";
                case Key.F9:  return Csi + "20~";
                case Key.F10: return Csi + "21~";
                case Key.F11: return Csi + "23~";
                case Key.F12: return Csi + "24~";

                case Key.Space: return ctrl ? Nul : null;
            }

            if (ctrl && !alt && key >= Key.A && key <= Key.Z)
                return ((char)(key - Key.A + 1)).ToString();

            // Alt+letter is escape then the letter, which is how a shell reads Meta.
            if (alt && !ctrl && key >= Key.A && key <= Key.Z)
                return Esc + (char)((shift ? 'A' : 'a') + (key - Key.A));

            return null;
        }

        private void Paste()
        {
            try
            {
                if (!Clipboard.ContainsText()) return;

                // A shell reads Enter as CR. Pasting CRLF would submit every line twice.
                string text = Clipboard.GetText().Replace("\r\n", "\r").Replace('\n', '\r');

                // Bracketed paste tells the shell this is pasted text, so PSReadLine holds a
                // multi-line paste as one editable block instead of running each line on arrival.
                Send(_buf.BracketedPaste ? Csi + "200~" + text + Csi + "201~" : text);
                _scroll = 0;
            }
            catch { /* another app holding the clipboard is not our problem */ }
        }

        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnGotKeyboardFocus(e);
            InvalidateVisual();
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            InvalidateVisual();
        }
    }
}
