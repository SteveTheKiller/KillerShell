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
            OscString,
            DcsEntry, DcsParam, DcsInt, DcsPass, DcsIgnore,
            SosPmApc,
        }

        private readonly IVtHandler _h = handler;
        private S _state = S.Ground;

        // CSI/DCS accumulators. Bounded on purpose: a hostile or corrupt stream must not be
        // able to make us allocate without limit, and no real sequence comes near these.
        private const int MaxParams = 32;
        private readonly int[] _pars = new int[MaxParams];
        private int _parCount;
        private bool _parSeen;          // distinguishes "CSI m" from "CSI 0 m"
        private char _prefix, _inter;

        private readonly StringBuilder _osc = new();
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

            // ESC and CAN/SUB abort whatever is in flight, from any state. This is what stops a
            // malformed sequence from swallowing everything after it.
            if (b == 0x1B) { Clear(); _state = S.Escape; return; }
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
                        case (byte)']': _oscCmd = 0; _oscCmdDone = false; _osc.Clear(); _state = S.OscString; break;
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

                case S.CsiEntry:
                    if (b < 0x20) { _h.Execute(b); break; }
                    if (b >= 0x3C && b <= 0x3F) { _prefix = (char)b; _state = S.CsiParam; break; }
                    if (b == ';' || (b >= '0' && b <= '9')) { _state = S.CsiParam; Param(b); break; }
                    if (b < 0x30) { _inter = (char)b; _state = S.CsiInt; break; }
                    Csi(b);
                    break;

                case S.CsiParam:
                    if (b < 0x20) { _h.Execute(b); break; }
                    if (b == ';' || (b >= '0' && b <= '9')) { Param(b); break; }
                    if (b >= 0x3C && b <= 0x3F) { _state = S.CsiIgnore; break; }   // prefix after params is malformed
                    if (b < 0x30) { _inter = (char)b; _state = S.CsiInt; break; }
                    Csi(b);
                    break;

                case S.CsiInt:
                    if (b < 0x20) { _h.Execute(b); break; }
                    if (b < 0x30) { _inter = (char)b; break; }
                    if (b < 0x40) { _state = S.CsiIgnore; break; }
                    Csi(b);
                    break;

                case S.CsiIgnore:
                    if (b < 0x20) { _h.Execute(b); break; }
                    if (b >= 0x40) _state = S.Ground;
                    break;

                case S.OscString:
                    // BEL is the old terminator, ST (ESC \) the standard one. ESC is caught
                    // above and lands us in Escape, where '\' dispatches nothing - so the
                    // string ends either way, which is what we want.
                    if (b == 0x07) { FlushOsc(); break; }
                    if (!_oscCmdDone)
                    {
                        if (b >= '0' && b <= '9') { _oscCmd = _oscCmd * 10 + (b - '0'); break; }
                        if (b == ';') { _oscCmdDone = true; break; }
                        _oscCmdDone = true;   // no numeric command, treat the rest as data
                    }
                    if (_osc.Length < 4096) _osc.Append((char)b);
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
            if (b == ';')
            {
                if (_parCount < MaxParams - 1) _parCount++;
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
            var pars = new int[n];
            Array.Copy(_pars, pars, n);
            _h.CsiDispatch((char)final, pars, _prefix, _inter);
            _state = S.Ground;
        }

        private void FlushOsc()
        {
            _h.OscDispatch(_oscCmd, _osc.ToString());
            _osc.Clear();
            _state = S.Ground;
        }

        private void Clear()
        {
            Array.Clear(_pars, 0, _pars.Length);
            _parCount = 0;
            _parSeen = false;
            _prefix = '\0';
            _inter = '\0';
        }
    }
}
