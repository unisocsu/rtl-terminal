using System;
using System.Collections.Generic;
using System.Text;

namespace RtlTerminal.Terminal
{
    /// <summary>
    /// Minimal but functional VT100/xterm-subset parser. Feed it characters as they arrive from
    /// ConPTY; it interprets control characters and CSI/OSC escape sequences and applies them to
    /// a <see cref="TerminalBuffer"/>, instead of just stripping them like a plain text log would.
    ///
    /// Covers: cursor movement (CUU/CUD/CUF/CUB/CUP/HVP), erase in line/display (EL/ED),
    /// SGR colors/bold/underline/reverse, cursor show/hide (DECTCEM), save/restore cursor,
    /// scroll region (DECSTBM), the common single-byte controls (BS, HT, LF, CR), and the
    /// alternate screen buffer (?47/?1047/?1049 h/l).
    ///
    /// Alternate screen buffer matters more than it might look: full-screen TUI apps (like an
    /// Ink-based CLI) assume their absolute cursor addressing (CUP) stays valid relative to a
    /// FIXED top-of-screen for their whole session. If their content is drawn into the same
    /// buffer as normal scrollback and a line-feed at the bottom row ever triggers a scroll,
    /// every row index the app assumes becomes wrong from that point on - which looks exactly
    /// like "an unrelated line got erased" days after the actual desync happened. Giving such
    /// apps their own isolated buffer (swapped back out when they exit) avoids that entirely.
    /// OSC sequences (window title, etc.) are recognized and skipped rather than leaking into
    /// the visible buffer.
    /// </summary>
    public sealed class VtParser
    {
        private enum State { Ground, Escape, Csi, Osc, OscEscape }

        private State _state = State.Ground;
        private readonly StringBuilder _paramBuffer = new();

        private readonly TerminalBuffer _mainBuffer;
        private TerminalBuffer? _altBuffer;
        private TerminalBuffer _active;

        /// <summary>Raised whenever the active buffer changes (entering/leaving the alternate
        /// screen). The view should re-attach its canvas to the new active buffer.</summary>
        public event Action<TerminalBuffer>? ActiveBufferChanged;

        public TerminalBuffer ActiveBuffer => _active;

        public VtParser(TerminalBuffer buffer)
        {
            _mainBuffer = buffer;
            _active = buffer;
        }

        /// <summary>Call when the terminal is resized, so both the main and (if present) the
        /// alternate buffer stay the same size - otherwise switching back to a stale-sized
        /// buffer would misrender.</summary>
        public void Resize(int columns, int rows)
        {
            _mainBuffer.Resize(columns, rows);
            _altBuffer?.Resize(columns, rows);
        }

        public void Feed(string text)
        {
            foreach (char c in text)
                FeedChar(c);
        }

        private void FeedChar(char c)
        {
            switch (_state)
            {
                case State.Ground:
                    HandleGround(c);
                    break;

                case State.Escape:
                    HandleEscape(c);
                    break;

                case State.Csi:
                    HandleCsi(c);
                    break;

                case State.Osc:
                    if (c == '\u0007') // BEL terminates OSC
                    {
                        _state = State.Ground;
                    }
                    else if (c == '\u001B')
                    {
                        _state = State.OscEscape;
                    }
                    // else: swallow OSC payload (window title, etc.)
                    break;

                case State.OscEscape:
                    // Expect '\\' to complete ST (ESC \\); either way, OSC is done.
                    _state = State.Ground;
                    break;
            }
        }

        private void HandleGround(char c)
        {
            switch (c)
            {
                case '\u001B':
                    _state = State.Escape;
                    return;
                case '\r':
                    _active.CarriageReturn();
                    return;
                case '\n':
                    _active.LineFeed();
                    return;
                case '\b':
                    _active.Backspace();
                    return;
                case '\t':
                    _active.Tab();
                    return;
                case '\u0007': // BEL
                    return;
                default:
                    if (c >= 0x20) // printable
                        _active.WriteChar(c);
                    return;
            }
        }

        private void HandleEscape(char c)
        {
            switch (c)
            {
                case '[':
                    _paramBuffer.Clear();
                    _state = State.Csi;
                    return;
                case ']': // OSC - Operating System Command
                case 'P': // DCS - Device Control String
                case '_': // APC - Application Program Command
                case '^': // PM - Privacy Message
                case 'X': // SOS - Start Of String
                    // All of these are "string" sequences terminated by BEL or ST (ESC \\), same
                    // as OSC. Previously only ']' was recognized here; any other introducer fell
                    // through to the default case below, which reset to Ground and let the
                    // sequence's payload bytes fall through as literal printable characters -
                    // getting written directly onto the screen at the cursor position (which is
                    // wherever the user was typing). Swallowing them here instead prevents that.
                    _state = State.Osc;
                    return;
                case '7': // DECSC save cursor
                    _active.SaveCursor();
                    _state = State.Ground;
                    return;
                case '8': // DECRC restore cursor
                    _active.RestoreCursor();
                    _state = State.Ground;
                    return;
                case 'M': // reverse index (scroll down) - approximate as cursor up
                    _active.MoveCursorUp(1);
                    _state = State.Ground;
                    return;
                default:
                    // Unsupported single-char escape (e.g. charset selection) - ignore.
                    _state = State.Ground;
                    return;
            }
        }

        private void HandleCsi(char c)
        {
            // Final byte for a CSI sequence is in the range 0x40-0x7E.
            if (c is >= (char)0x40 and <= (char)0x7E)
            {
                ExecuteCsi(c, _paramBuffer.ToString());
                _state = State.Ground;
                return;
            }
            _paramBuffer.Append(c);
        }

        private void ExecuteCsi(char final, string paramText)
        {
            bool isPrivate = paramText.StartsWith("?");
            string cleanParams = isPrivate ? paramText[1..] : paramText;
            List<int> ps = ParseParams(cleanParams);

            int PDef1(int index) => index < ps.Count && ps[index] > 0 ? ps[index] : 1;

            switch (final)
            {
                case 'A': _active.MoveCursorUp(PDef1(0)); break;
                case 'B': _active.MoveCursorDown(PDef1(0)); break;
                case 'C': _active.MoveCursorForward(PDef1(0)); break;
                case 'D': _active.MoveCursorBack(PDef1(0)); break;

                case 'H':
                case 'f':
                    {
                        int row = ps.Count > 0 && ps[0] > 0 ? ps[0] : 1;
                        int col = ps.Count > 1 && ps[1] > 0 ? ps[1] : 1;
                        _active.SetCursorPosition(row, col);
                        break;
                    }

                case 'J':
                    _active.EraseInDisplay(ps.Count > 0 ? ps[0] : 0);
                    break;

                case 'K':
                    _active.EraseInLine(ps.Count > 0 ? ps[0] : 0);
                    break;

                case 'm':
                    if (ps.Count == 0)
                    {
                        _active.ResetSgr();
                    }
                    else
                    {
                        foreach (int code in ps) _active.ApplySgr(code);
                    }
                    break;

                case 's':
                    _active.SaveCursor();
                    break;

                case 'u':
                    _active.RestoreCursor();
                    break;

                case 'r':
                    {
                        int top = ps.Count > 0 && ps[0] > 0 ? ps[0] : 1;
                        int bottom = ps.Count > 1 && ps[1] > 0 ? ps[1] : _active.Rows;
                        _active.SetScrollRegion(top, bottom);
                        break;
                    }

                case 'h':
                    if (isPrivate) HandlePrivateMode(ps, enable: true);
                    break;

                case 'l':
                    if (isPrivate) HandlePrivateMode(ps, enable: false);
                    break;

                // Unsupported final bytes (device queries, etc.) are silently ignored.
            }
        }

        private void HandlePrivateMode(List<int> ps, bool enable)
        {
            foreach (int code in ps)
            {
                switch (code)
                {
                    case 25: // DECTCEM - cursor visibility
                        _active.SetCursorVisible(enable);
                        break;

                    case 47:
                    case 1047:
                    case 1049:
                        // Alternate screen buffer. 1049 additionally saves/restores the cursor.
                        if (enable) SwitchToAltBuffer(saveCursor: code == 1049);
                        else SwitchToMainBuffer(restoreCursor: code == 1049);
                        break;

                    // Other private modes (bracketed paste 2004, application cursor keys 1,
                    // mouse reporting 1000/1002/1003/1006, etc.) aren't needed for basic
                    // shell/CLI use and are silently ignored rather than mis-handled.
                }
            }
        }

        private void SwitchToAltBuffer(bool saveCursor)
        {
            if (ReferenceEquals(_active, _altBuffer)) return; // already in alt screen

            if (saveCursor) _mainBuffer.SaveCursor();

            _altBuffer ??= new TerminalBuffer(_mainBuffer.Columns, _mainBuffer.Rows);
            if (_altBuffer.Columns != _mainBuffer.Columns || _altBuffer.Rows != _mainBuffer.Rows)
                _altBuffer.Resize(_mainBuffer.Columns, _mainBuffer.Rows);

            _altBuffer.EraseInDisplay(2); // start the alt screen blank, like a real terminal does
            _active = _altBuffer;
            ActiveBufferChanged?.Invoke(_active);
        }

        private void SwitchToMainBuffer(bool restoreCursor)
        {
            if (ReferenceEquals(_active, _mainBuffer)) return; // already on main screen

            _active = _mainBuffer;
            if (restoreCursor) _mainBuffer.RestoreCursor();
            ActiveBufferChanged?.Invoke(_active);
        }

        private static List<int> ParseParams(string text)
        {
            var result = new List<int>();
            if (text.Length == 0) return result;

            foreach (string part in text.Split(';'))
            {
                result.Add(int.TryParse(part, out int v) ? v : 0);
            }
            return result;
        }
    }
}
