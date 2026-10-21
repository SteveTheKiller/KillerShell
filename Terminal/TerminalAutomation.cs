using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using System.Windows.Threading;
using KillerShell.Shell;

namespace KillerShell.Terminal
{
    internal sealed partial class TerminalControl
    {
        private DispatcherTimer? _automationEvents;
        private bool _automationTextChanged, _automationSelectionChanged;
        private TextSnapshot? _automationSnapshot;
        private readonly Action<AutomationPeer, AutomationEvents> _automationEventSink;

        protected override AutomationPeer OnCreateAutomationPeer() => new TerminalPeer(this);

        private TextSnapshot AutomationSnapshot()
        {
            if (_automationSnapshot == null || _automationSnapshot.Version != _buf.Version)
                _automationSnapshot = new TextSnapshot(_buf);
            return _automationSnapshot;
        }

        private void QueueAutomationChange(bool text = false, bool selection = false)
        {
            if (_closing || UIElementAutomationPeer.FromElement(this) is not TerminalPeer) return;
            _automationTextChanged |= text;
            _automationSelectionChanged |= selection;
            if (_automationEvents == null)
            {
                _automationEvents = new DispatcherTimer(DispatcherPriority.Background)
                { Interval = TimeSpan.FromMilliseconds(250) };
                _automationEvents.Tick += (_, _) =>
                {
                    _automationEvents.Stop();
                    bool textChanged = _automationTextChanged, selectionChanged = _automationSelectionChanged;
                    _automationTextChanged = _automationSelectionChanged = false;
                    if (!_closing && UIElementAutomationPeer.FromElement(this) is TerminalPeer peer)
                    {
                        if (textChanged && AutomationPeer.ListenerExists(AutomationEvents.TextPatternOnTextChanged))
                            _automationEventSink(peer, AutomationEvents.TextPatternOnTextChanged);
                        if (selectionChanged && AutomationPeer.ListenerExists(AutomationEvents.TextPatternOnTextSelectionChanged))
                            _automationEventSink(peer, AutomationEvents.TextPatternOnTextSelectionChanged);
                    }
                };
            }
            // Do not restart an enabled timer: continuous output still produces bounded updates.
            if (!_automationEvents.IsEnabled) _automationEvents.Start();
        }

        private sealed class TextSnapshot
        {
            internal readonly int Version;
            internal readonly string Text;
            internal readonly Cell[][] Lines;
            internal readonly object[] LineIdentities;
            internal readonly int[] Starts;
            private readonly int[][] _offsets;
            private readonly Dictionary<object, int> _lineNumbers = new();

            internal TextSnapshot(TerminalBuffer buffer)
            {
                Version = buffer.Version;
                Lines = new Cell[buffer.TotalLines][];
                LineIdentities = new object[Lines.Length];
                Starts = new int[Lines.Length];
                _offsets = new int[Lines.Length][];
                var text = new StringBuilder();
                for (int line = 0; line < Lines.Length; line++)
                {
                    var cells = Lines[line] = buffer.LineAt(line);
                    object identity = LineIdentities[line] = buffer.LineIdentity(cells);
                    _lineNumbers[identity] = line;
                    Starts[line] = text.Length;
                    var offsets = _offsets[line] = new int[cells.Length + 1];
                    for (int col = 0; col < cells.Length; col++)
                    {
                        if (cells[col].IsContinuation)
                        {
                            offsets[col] = offsets[col - 1];
                            continue;
                        }
                        offsets[col] = text.Length;
                        text.Append((cells[col].Flags & CellFlags.Hidden) != 0
                            ? new string(' ', Math.Max(1, (int)cells[col].Width)) : cells[col].Text);
                    }
                    while (text.Length > Starts[line] && text[text.Length - 1] == ' ') text.Length--;
                    offsets[cells.Length] = text.Length;
                    for (int col = 0; col < cells.Length; col++) offsets[col] = Math.Min(offsets[col], text.Length);
                    if (line + 1 < Lines.Length) text.Append("\r\n");
                }
                Text = text.ToString();
            }

            internal int LineNumber(object identity) => _lineNumbers.TryGetValue(identity, out int line) ? line : 0;
            internal int Offset(int line, int col)
            {
                line = Math.Max(0, Math.Min(line, Lines.Length - 1));
                return _offsets[line][Math.Max(0, Math.Min(col, Lines[line].Length))];
            }

            internal (int Line, int Col) Position(int offset, bool end = false)
            {
                offset = Math.Max(0, Math.Min(offset, Text.Length));
                int line = Array.BinarySearch(Starts, offset);
                if (line < 0) line = Math.Max(0, ~line - 1);
                var offsets = _offsets[line];
                int low = 0, high = offsets.Length - 1;
                while (low < high)
                {
                    int middle = low + (high - low) / 2;
                    if (offsets[middle] < offset) low = middle + 1; else high = middle;
                }
                if (!end && low > 0 && offsets[low] > offset) low--;
                if (low < Lines[line].Length && Lines[line][low].IsContinuation) low--;
                return (line, low);
            }

            internal int[] Boundaries(TextUnit unit)
            {
                if (unit == TextUnit.Document || unit == TextUnit.Page)
                    return new[] { 0, Text.Length };
                var boundaries = new List<int> { 0 };
                if (unit == TextUnit.Character)
                {
                    foreach (var line in _offsets)
                        foreach (int offset in line)
                            if (offset > boundaries[boundaries.Count - 1]) boundaries.Add(offset);
                }
                else if (unit == TextUnit.Line || unit == TextUnit.Paragraph || unit == TextUnit.Format)
                {
                    for (int line = 1; line < Starts.Length; line++) boundaries.Add(Starts[line]);
                }
                else
                {
                    int offset = 0;
                    while (offset < Text.Length)
                    {
                        bool whitespace = char.IsWhiteSpace(Text[offset]);
                        int next = offset + 1;
                        if (char.IsHighSurrogate(Text[offset]) && next < Text.Length && char.IsLowSurrogate(Text[next])) next++;
                        else if (Text[offset] == '\r' && next < Text.Length && Text[next] == '\n') next++;
                        if (unit == TextUnit.Character || next == Text.Length || whitespace != char.IsWhiteSpace(Text[next]))
                            if (next < Text.Length) boundaries.Add(next);
                        offset = next;
                    }
                }
                if (boundaries[boundaries.Count - 1] != Text.Length) boundaries.Add(Text.Length);
                return boundaries.ToArray();
            }
        }

        private sealed class TerminalPeer(TerminalControl terminal) : FrameworkElementAutomationPeer(terminal), ITextProvider
        {
            internal readonly TerminalControl Terminal = terminal;
            internal IRawElementProviderSimple RawProvider => ProviderFromPeer(this);
            protected override string GetClassNameCore() => "Terminal";
            protected override string GetNameCore()
            {
                string name = base.GetNameCore();
                return string.IsNullOrEmpty(name) ? MainWindow.LocStatic("Str_Ks_ScopeTerminal") : name;
            }
            protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
            protected override bool IsControlElementCore() => true;
            protected override bool IsContentElementCore() => true;
            public override object GetPattern(PatternInterface pattern) => pattern == PatternInterface.Text ? this : base.GetPattern(pattern);
            public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;
            public ITextRangeProvider DocumentRange => new TerminalRange(this, 0, Terminal.AutomationSnapshot().Text.Length);
            public ITextRangeProvider[] GetSelection()
            {
                if (Terminal._hasSelection)
                {
                    Terminal.Ordered(out int l1, out int c1, out int l2, out int c2);
                    return new ITextRangeProvider[] { new TerminalRange(this, l1, c1, l2, c2) };
                }
                int line = Terminal._buf.ScrollbackCount + Terminal._buf.CursorRow;
                return new ITextRangeProvider[] { new TerminalRange(this, line, Terminal._buf.CursorCol, line, Terminal._buf.CursorCol) };
            }
            public ITextRangeProvider[] GetVisibleRanges()
            {
                int first = Math.Max(0, Terminal._buf.ScrollbackCount - Terminal._scroll);
                int last = Math.Min(Terminal._buf.TotalLines - 1, first + Terminal._buf.Rows - 1);
                return new ITextRangeProvider[] { new TerminalRange(this, first, 0, last, Terminal._buf.Cols) };
            }
            public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) => throw new ArgumentException(nameof(childElement));
            public ITextRangeProvider RangeFromPoint(Point screenLocation)
            {
                Terminal.CellAt(Terminal.PointFromScreen(screenLocation), out int line, out int col);
                return new TerminalRange(this, line, col, line, col);
            }
        }

        private sealed class TerminalRange : ITextRangeProvider
        {
            private readonly TerminalPeer _peer;
            private object _startLine = null!, _endLine = null!;
            private int _startCol, _endCol;
            private TerminalControl Terminal => _peer.Terminal;
            private TextSnapshot Snapshot => Terminal.AutomationSnapshot();

            internal TerminalRange(TerminalPeer peer, int start, int end)
            {
                _peer = peer;
                SetOffsets(start, end);
            }
            internal TerminalRange(TerminalPeer peer, int l1, int c1, int l2, int c2)
            {
                _peer = peer;
                var snapshot = Snapshot;
                _startLine = snapshot.LineIdentities[Math.Max(0, Math.Min(l1, snapshot.Lines.Length - 1))];
                _endLine = snapshot.LineIdentities[Math.Max(0, Math.Min(l2, snapshot.Lines.Length - 1))];
                _startCol = c1; _endCol = c2;
            }
            private (int Start, int End) Offsets()
            {
                var snapshot = Snapshot;
                int start = snapshot.Offset(snapshot.LineNumber(_startLine), _startCol);
                int end = snapshot.Offset(snapshot.LineNumber(_endLine), _endCol);
                return (Math.Min(start, end), Math.Max(start, end));
            }
            private void SetOffsets(int start, int end)
            {
                var snapshot = Snapshot;
                var first = snapshot.Position(Math.Min(start, end));
                var last = snapshot.Position(Math.Max(start, end), start != end);
                _startLine = snapshot.LineIdentities[first.Line]; _startCol = first.Col;
                _endLine = snapshot.LineIdentities[last.Line]; _endCol = last.Col;
            }
            private TerminalRange Other(ITextRangeProvider range)
                => range is TerminalRange other && other._peer == _peer ? other : throw new ArgumentException(nameof(range));
            private int Endpoint(TextPatternRangeEndpoint endpoint)
            {
                var offsets = Offsets();
                return endpoint == TextPatternRangeEndpoint.Start ? offsets.Start : offsets.End;
            }
            private void SetEndpoint(TextPatternRangeEndpoint endpoint, int value)
            {
                var offsets = Offsets();
                if (endpoint == TextPatternRangeEndpoint.Start) SetOffsets(value, Math.Max(value, offsets.End));
                else SetOffsets(Math.Min(offsets.Start, value), value);
            }
            public ITextRangeProvider Clone() => new TerminalRange(_peer, Snapshot.LineNumber(_startLine), _startCol,
                Snapshot.LineNumber(_endLine), _endCol);
            public bool Compare(ITextRangeProvider range)
                => range is TerminalRange other && other._peer == _peer && Offsets() == other.Offsets();
            public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
                => Endpoint(endpoint).CompareTo(Other(targetRange).Endpoint(targetEndpoint));
            public void ExpandToEnclosingUnit(TextUnit unit)
            {
                int offset = Offsets().Start;
                int[] boundaries = Snapshot.Boundaries(unit);
                int index = Array.BinarySearch(boundaries, offset);
                if (index < 0) index = ~index - 1;
                index = Math.Max(0, Math.Min(index, boundaries.Length - 2));
                SetOffsets(boundaries[index], boundaries[Math.Min(index + 1, boundaries.Length - 1)]);
            }
            public ITextRangeProvider? FindAttribute(int attributeId, object value, bool backward)
                => attributeId == TextPatternIdentifiers.IsReadOnlyAttribute.Id && value is true ? Clone() : null;
            public ITextRangeProvider? FindText(string text, bool backward, bool ignoreCase)
            {
                if (string.IsNullOrEmpty(text)) throw new ArgumentException(nameof(text));
                var offsets = Offsets();
                string content = Snapshot.Text.Substring(offsets.Start, offsets.End - offsets.Start);
                var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                int found = backward ? content.LastIndexOf(text, comparison) : content.IndexOf(text, comparison);
                return found < 0 ? null : new TerminalRange(_peer, offsets.Start + found, offsets.Start + found + text.Length);
            }
            public object GetAttributeValue(int attributeId)
                => attributeId == TextPatternIdentifiers.IsReadOnlyAttribute.Id ? true : AutomationElementIdentifiers.NotSupported;
            public double[] GetBoundingRectangles()
            {
                var result = new List<double>();
                if (PresentationSource.FromVisual(Terminal) == null) return result.ToArray();
                var snapshot = Snapshot;
                int l1 = snapshot.LineNumber(_startLine), l2 = snapshot.LineNumber(_endLine);
                int visible = Math.Max(0, Terminal._buf.ScrollbackCount - Terminal._scroll);
                for (int line = Math.Max(l1, visible); line <= Math.Min(l2, visible + Terminal._buf.Rows - 1); line++)
                {
                    int from = Math.Max(0, Math.Min(Terminal._buf.Cols, line == l1 ? _startCol : 0));
                    int to = Math.Max(0, Math.Min(Terminal._buf.Cols, line == l2 ? _endCol : Terminal._buf.Cols));
                    if (to < from) continue;
                    Point first = Terminal.PointToScreen(new Point(LeftInset + from * Terminal._cellW, (line - visible) * Terminal._cellH));
                    Point last = Terminal.PointToScreen(new Point(LeftInset + to * Terminal._cellW, (line - visible + 1) * Terminal._cellH));
                    result.Add(first.X); result.Add(first.Y); result.Add(Math.Max(1, last.X - first.X)); result.Add(last.Y - first.Y);
                }
                return result.ToArray();
            }
            public IRawElementProviderSimple[] GetChildren() => Array.Empty<IRawElementProviderSimple>();
            public IRawElementProviderSimple GetEnclosingElement() => _peer.RawProvider;
            public string GetText(int maxLength)
            {
                if (maxLength < -1) throw new ArgumentOutOfRangeException(nameof(maxLength));
                var offsets = Offsets();
                string content = Snapshot.Text;
                int length = offsets.End - offsets.Start;
                if (maxLength >= 0) length = Math.Min(length, maxLength);
                if (length > 0 && offsets.Start + length < content.Length && char.IsHighSurrogate(content[offsets.Start + length - 1])) length--;
                return content.Substring(offsets.Start, length);
            }
            public int Move(TextUnit unit, int count)
            {
                if (count == 0) return 0;
                bool empty = Offsets().Start == Offsets().End;
                if (!empty) ExpandToEnclosingUnit(unit);
                int[] boundaries = Snapshot.Boundaries(unit);
                int offset = Offsets().Start;
                int index = Array.BinarySearch(boundaries, offset);
                if (index < 0) index = count > 0 ? ~index - 1 : ~index;
                int maximum = Math.Max(0, boundaries.Length - (empty ? 1 : 2));
                int next = (int)Math.Max(0L, Math.Min(maximum, (long)index + count));
                SetOffsets(boundaries[next], empty ? boundaries[next] : boundaries[Math.Min(next + 1, boundaries.Length - 1)]);
                return next - index;
            }
            public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
            {
                if (count == 0) return 0;
                int[] boundaries = Snapshot.Boundaries(unit);
                int index = Array.BinarySearch(boundaries, Endpoint(endpoint));
                if (index < 0) index = count > 0 ? ~index - 1 : ~index;
                int next = (int)Math.Max(0L, Math.Min(boundaries.Length - 1, (long)index + count));
                SetEndpoint(endpoint, boundaries[next]);
                return next - index;
            }
            public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
                => SetEndpoint(endpoint, Other(targetRange).Endpoint(targetEndpoint));
            public void Select()
            {
                var snapshot = Snapshot;
                Terminal._selAnchorLine = snapshot.LineNumber(_startLine); Terminal._selAnchorCol = _startCol;
                Terminal._selHeadLine = snapshot.LineNumber(_endLine); Terminal._selHeadCol = _endCol;
                Terminal._hasSelection = Offsets().Start != Offsets().End;
                Terminal._selecting = false;
                Terminal.InvalidateVisual();
                Terminal.QueueAutomationChange(selection: true);
            }
            public void AddToSelection()
            {
                if (Terminal._hasSelection && !Compare(_peer.GetSelection()[0])) throw new InvalidOperationException();
                Select();
            }
            public void RemoveFromSelection()
            {
                if (!Compare(_peer.GetSelection()[0])) throw new InvalidOperationException();
                Terminal.ClearSelection();
            }
            public void ScrollIntoView(bool alignToTop)
            {
                int line = Snapshot.LineNumber(alignToTop ? _startLine : _endLine);
                int first = alignToTop ? line : line - Terminal._buf.Rows + 1;
                Terminal._scroll = Math.Max(0, Math.Min(Terminal._buf.ScrollbackCount, Terminal._buf.ScrollbackCount - first));
                Terminal.InvalidateVisual();
            }
        }
    }
}
