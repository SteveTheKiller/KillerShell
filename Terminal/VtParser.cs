using System;
using System.Text;

// The VT stream parser: bytes in from ConPTY, calls out to whatever is drawing.
//
// This is Paul Williams' DEC ANSI state machine, the same shape every serious terminal uses.
// It is written as a state machine rather than as a pile of regexes or a StartsWith chain for
// one reason: the stream ARRIVES IN ARBITRARY CHUNKS. A pipe read can split "ESC [ 3 1 m"
// after the '3', and anything that pattern-matches a whole buffer at a time gets that wrong
// and paints the rest of the screen the wrong color. A state machine simply resumes.
//
// UTF-8 decoding lives here too, for the same reason: a multi-byte character can straddle a
// read, so the decoder has to keep its partial sequence between calls.
namespace KillerShell.Terminal
{
    /// <summary>What the parser drives. Implemented by the screen buffer.</summary>
    internal interface IVtHandler
    {
        /// <summary>A printable character (already decoded from UTF-8).</summary>
        void Print(int codepoint);

        /// <summary>A C0/C1 control: CR, LF, BS, TAB, BEL and friends.</summary>
        void Execute(byte control);

        /// <summary>
        /// A CSI sequence. <paramref name="prefix"/> is the private marker ('?' for DEC private
        /// modes, '&gt;' etc.), 0 when there is none.
        /// </summary>
        void CsiDispatch(char final, int[] pars, char prefix, char intermediate);

        /// <summary>An ESC sequence that is not CSI, such as ESC 7 (save cursor).</summary>
        void EscDispatch(char final, char intermediate);

        /// <summary>An OSC string: window title, working directory, hyperlinks.</summary>
        void OscDispatch(int command, string data);
    }

    internal sealed class VtParser(IVtHandler handler)
    {
        private enum S
        {
            Ground, Escape, EscInt,
            CsiEntry, CsiParam, CsiInt, CsiIgnore,
            OscString, OscEscape,
            DcsEntry, DcsParam, DcsInt, DcsPass, DcsIgnore,
            SosPmApc,
        }

        private readonly IVtHandler _h = handler;
        private S _state = S.Ground;

        // CSI/DCS accumulators. Bounded on purpose: a hostile or corrupt stream must not be
        // able to make us allocate without limit, and no real sequence comes near these.
        private const int MaxParams = 32;
        private readonly int[] _pars = new int[MaxParams];
        // True where a parameter was introduced by ':' (an ITU T.416 subparameter) rather than ';'.
        private readonly bool[] _sub = new bool[MaxParams];
        private bool _subSeen;
        private int _parCount;
        private bool _parSeen;          // distinguishes "CSI m" from "CSI 0 m"
        private char _prefix, _inter;

        // Retain raw bytes across reads and decode the complete payload when it terminates.
        private readonly byte[] _osc = new byte[4096];
        private int _oscLength;
        private int _oscCmd;
        private bool _oscCmdDone;

        // UTF-8 continuation state, kept across chunks.
        private int _utfLeft, _utfCp, _utfMin;

        public void Feed(byte[] buf, int count)
        {
            for (int i = 0; i < count; i++) Step(buf[i]);
        }

        private void Step(byte b)
        {
            // A mid-character UTF-8 byte belongs to the decoder no matter what state we are in,
            // EXCEPT that a control byte cancels it - a truncated sequence must not eat the CR
            // that follows it and desync the whole screen.
            if (_utfLeft > 0)
            {
                if ((b & 0xC0) == 0x80)
                {
                    _utfCp = (_utfCp << 6) | (b & 0x3F);
                    if (--_utfLeft == 0)
                    {
                        // Overlong forms and surrogates are rejected: they are the classic way
                        // to smuggle a control character past a naive decoder.
                        if (_utfCp < _utfMin || (_utfCp >= 0xD800 && _utfCp <= 0xDFFF) || _utfCp > 0x10FFFF)
                            _h.Print(0xFFFD);
                        else
                            _h.Print(_utfCp);
                    }
                    return;
                }
                _utfLeft = 0;
                _h.Print(0xFFFD);
                // fall through and handle b normally
            }

            // An ESC inside OSC may start ST. Keep the payload until the next byte confirms it.
            if (_state == S.OscEscape)
            {
                if (b == '\\') { FlushOsc(); return; }
                _state = S.Escape;
            }

            // Other ESC sequences and CAN/SUB abort whatever is in flight. This stops a
            // malformed sequence from swallowing everything after it.
            if (b == 0x1B) { Clear(); _state = _state == S.OscString ? S.OscEscape : S.Escape; return; }
            if (b == 0x18 || b == 0x1A) { _state = S.Ground; _h.Execute(b); return; }

            switch (_state)
            {
                case S.Ground: Ground(b); break;

                case S.Escape:
                    if (b < 0x20) { _h.Execute(b); break; }
                    if (b < 0x30) { _inter = (char)b; _state = S.EscInt; break; }
                    switch (b)
                    {
                        case (byte)'[': _state = S.CsiEntry; break;
                        case (byte)']': _oscCmd = 0; _oscCmdDone = false; _oscLength = 0; _state = S.OscString; break;
                        case (byte)'P': _state = S.DcsEntry; break;
                        case (byte)'X': case (byte)'^': case (byte)'_': _state = S.SosPmApc; break;
                        default: _h.EscDispatch((char)b, '\0'); _state = S.Ground; break;
                    }
                    break;

                case S.EscInt:
                    if (b < 0x20) { _h.Execute(b); break; }
                    if (b < 0x30) { _inter = (char)b; break; }
                    _h.EscDispatch((char)b, _inter);
                    _state = S.Ground;
                    break;

                // Only 0x40-0x7E ends a CSI sequence. Every parameter byte (0x30-0x3F, the colon
                // included) stays inside it, so nothing between ESC [ and the final byte can print.
                case S.CsiEntry:
                    if (b < 0x20) { _h.Execute(b); break; }
                    if (b >= 0x3C && b <= 0x3F) { _prefix = (char)b; _state = S.CsiParam; break; }
                    if (b <= 0x3B && b >= 0x30) { _state = S.CsiParam; Param(b); break; }
                    if (b < 0x30) { _inter = (char)b; _state = S.CsiInt; break; }
                    if (b <= 0x7E) { Csi(b); break; }
                    if (b != 0x7F) _state = S.CsiIgnore;
                    break;

                case S.CsiParam:
                    if (b < 0x20) { _h.Execute(b); break; }
                    if (b <= 0x3B && b >= 0x30) { Param(b); break; }
                    if (b >= 0x3C && b <= 0x3F) { _state = S.CsiIgnore; break; }   // prefix after params is malformed
                    if (b < 0x30) { _inter = (char)b; _state = S.CsiInt; break; }
                    if (b <= 0x7E) { Csi(b); break; }
                    if (b != 0x7F) _state = S.CsiIgnore;
                    break;

                case S.CsiInt:
                    if (b < 0x20) { _h.Execute(b); break; }
                    if (b < 0x30) { _inter = (char)b; break; }
                    if (b < 0x40 || b > 0x7E) { if (b != 0x7F) _state = S.CsiIgnore; break; }
                    Csi(b);
                    break;

                case S.CsiIgnore:
                    if (b < 0x20) { _h.Execute(b); break; }
                    if (b >= 0x40 && b <= 0x7E) _state = S.Ground;
                    break;

                case S.OscString:
                    // BEL terminates immediately; ST (ESC \) is completed above.
                    if (b == 0x07) { FlushOsc(); break; }
                    if (!_oscCmdDone)
                    {
                        if (b >= '0' && b <= '9') { _oscCmd = _oscCmd * 10 + (b - '0'); break; }
                        if (b == ';') { _oscCmdDone = true; break; }
                        _oscCmdDone = true;   // no numeric command, treat the rest as data
                    }
                    if (_oscLength < _osc.Length) _osc[_oscLength++] = b;
                    break;

                // DCS carries sixel graphics and terminfo queries. Consumed and dropped: a file
                // browser's shell has no use for either, and swallowing them correctly matters
                // more than acting on them - an unconsumed DCS body would print as garbage.
                case S.DcsEntry:
                    if (b >= 0x3C && b <= 0x3F) { _state = S.DcsParam; break; }
                    if (b == ';' || (b >= '0' && b <= '9')) { _state = S.DcsParam; break; }
                    if (b < 0x30) { _state = S.DcsInt; break; }
                    _state = S.DcsPass;
                    break;
                case S.DcsParam:
                    if (b == ';' || (b >= '0' && b <= '9')) break;
                    if (b < 0x30) { _state = S.DcsInt; break; }
                    _state = b < 0x40 ? S.DcsIgnore : S.DcsPass;
                    break;
                case S.DcsInt:
                    if (b < 0x30) break;
                    _state = b < 0x40 ? S.DcsIgnore : S.DcsPass;
                    break;
                case S.DcsPass:
                case S.DcsIgnore:
                case S.SosPmApc:
                    break;   // ends at the ESC that starts ST, handled above
            }
        }

        private void Ground(byte b)
        {
            if (b < 0x20 || b == 0x7F) { _h.Execute(b); return; }

            if (b < 0x80) { _h.Print(b); return; }

            // Start of a UTF-8 sequence. _utfMin is the smallest codepoint this length may
            // legally encode, which is how overlong forms get caught when it completes.
            if ((b & 0xE0) == 0xC0)      { _utfLeft = 1; _utfCp = b & 0x1F; _utfMin = 0x80; }
            else if ((b & 0xF0) == 0xE0) { _utfLeft = 2; _utfCp = b & 0x0F; _utfMin = 0x800; }
            else if ((b & 0xF8) == 0xF0) { _utfLeft = 3; _utfCp = b & 0x07; _utfMin = 0x10000; }
            else _h.Print(0xFFFD);       // a stray continuation byte or an illegal lead
        }

        private void Param(byte b)
        {
            _parSeen = true;
            if (b == ';' || b == ':')
            {
                if (_parCount < MaxParams - 1)
                {
                    _parCount++;
                    _sub[_parCount] = b == ':';
                    if (b == ':') _subSeen = true;
                }
                return;
            }
            if (_parCount >= MaxParams) return;
            // Saturating rather than wrapping: "CSI 99999999999 A" should move the cursor a
            // long way, not a negative distance.
            long v = (long)_pars[_parCount] * 10 + (b - '0');
            _pars[_parCount] = v > 65535 ? 65535 : (int)v;
        }

        private void Csi(byte final)
        {
            int n = _parSeen ? _parCount + 1 : 0;
            int[] pars;
            if (_subSeen)
            {
                // Subparameters are only understood in SGR. Any other sequence carrying them is
                // consumed whole and not acted on.
                _state = S.Ground;
                if (final != 'm') return;
                pars = FlattenSgr(n);
                // Nothing usable left. An empty SGR would mean reset, which was not asked for.
                if (pars.Length == 0) return;
            }
            else
            {
                pars = new int[n];
                Array.Copy(_pars, pars, n);
            }
            _h.CsiDispatch((char)final, pars, _prefix, _inter);
            _state = S.Ground;
        }

        /// <summary>
        /// Rewrites colon SGR forms into the semicolon forms the handler reads: 38:2::r:g:b and
        /// 38:2:r:g:b become 38;2;r;g;b, 38:5:n becomes 38;5;n (likewise 48). Underline color (58)
        /// is dropped, and any other attribute with subparameters keeps only its main number, so
        /// 4:3 is plain underline. A subparameter never becomes an attribute of its own.
        /// </summary>
        private int[] FlattenSgr(int n)
        {
            var output = new System.Collections.Generic.List<int>(n);
            int i = 0;
            while (i < n)
            {
                int end = i + 1;
                while (end < n && _sub[end]) end++;
                int len = end - i, main = _pars[i];
                if (len == 1) output.Add(main);
                else if ((main == 38 || main == 48) && _pars[i + 1] == 2 && (len == 5 || len == 6))
                {
                    int rgb = end - 3;
                    output.Add(main); output.Add(2);
                    output.Add(_pars[rgb]); output.Add(_pars[rgb + 1]); output.Add(_pars[rgb + 2]);
                }
                else if ((main == 38 || main == 48) && _pars[i + 1] == 5 && len == 3)
                {
                    output.Add(main); output.Add(5); output.Add(_pars[i + 2]);
                }
                else if (main != 38 && main != 48 && main != 58) output.Add(main);
                i = end;
            }
            return output.ToArray();
        }

        private void FlushOsc()
        {
            _h.OscDispatch(_oscCmd, Encoding.UTF8.GetString(_osc, 0, _oscLength));
            _oscLength = 0;
            _state = S.Ground;
        }

        private void Clear()
        {
            Array.Clear(_pars, 0, _pars.Length);
            Array.Clear(_sub, 0, _sub.Length);
            _subSeen = false;
            _parCount = 0;
            _parSeen = false;
            _prefix = '\0';
            _inter = '\0';
        }
    }
}
