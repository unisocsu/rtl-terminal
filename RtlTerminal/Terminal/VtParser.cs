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
    /// scroll region (DECSTBM), and the common single-byte controls (BS, HT, LF, CR).
    /// OSC sequences (window title, etc.) are recognized and skipped rather than leaking into
    /// the visible buffer.
    /// </summary>
    public sealed class VtParser
    {
        private enum State { Ground, Escape, Csi, Osc, OscEscape }

        private State _state = State.Ground;
        private readonly StringBuilder _paramBuffer = new();
        private readonly TerminalBuffer _buffer;

        public VtParser(TerminalBuffer buffer)
        {
            _buffer = buffer;
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
                    _buffer.CarriageReturn();
                    return;
                case '\n':
                    _buffer.LineFeed();
                    return;
                case '\b':
                    _buffer.Backspace();
                    return;
                case '\t':
                    _buffer.Tab();
                    return;
                case '\u0007': // BEL
                    return;
                default:
                    if (c >= 0x20) // printable
                        _buffer.WriteChar(c);
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
                case ']':
                    _state = State.Osc;
                    return;
                case '7': // DECSC save cursor
                    _buffer.SaveCursor();
                    _state = State.Ground;
                    return;
                case '8': // DECRC restore cursor
                    _buffer.RestoreCursor();
                    _state = State.Ground;
                    return;
                case 'M': // reverse index (scroll down) - approximate as cursor up
                    _buffer.MoveCursorUp(1);
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

            int P(int index, int def = 0) => index < ps.Count && ps[index] > 0 ? ps[index] : (index < ps.Count ? def : def);
            int PDef1(int index) => index < ps.Count && ps[index] > 0 ? ps[index] : 1;

            switch (final)
            {
                case 'A': _buffer.MoveCursorUp(PDef1(0)); break;
                case 'B': _buffer.MoveCursorDown(PDef1(0)); break;
                case 'C': _buffer.MoveCursorForward(PDef1(0)); break;
                case 'D': _buffer.MoveCursorBack(PDef1(0)); break;

                case 'H':
                case 'f':
                    {
                        int row = ps.Count > 0 && ps[0] > 0 ? ps[0] : 1;
                        int col = ps.Count > 1 && ps[1] > 0 ? ps[1] : 1;
                        _buffer.SetCursorPosition(row, col);
                        break;
                    }

                case 'J':
                    _buffer.EraseInDisplay(ps.Count > 0 ? ps[0] : 0);
                    break;

                case 'K':
                    _buffer.EraseInLine(ps.Count > 0 ? ps[0] : 0);
                    break;

                case 'm':
                    if (ps.Count == 0)
                    {
                        _buffer.ResetSgr();
                    }
                    else
                    {
                        foreach (int code in ps) _buffer.ApplySgr(code);
                    }
                    break;

                case 's':
                    _buffer.SaveCursor();
                    break;

                case 'u':
                    _buffer.RestoreCursor();
                    break;

                case 'r':
                    {
                        int top = ps.Count > 0 && ps[0] > 0 ? ps[0] : 1;
                        int bottom = ps.Count > 1 && ps[1] > 0 ? ps[1] : _buffer.Rows;
                        _buffer.SetScrollRegion(top, bottom);
                        break;
                    }

                case 'h':
                    if (isPrivate && ps.Count > 0 && ps[0] == 25) _buffer.SetCursorVisible(true);
                    break;

                case 'l':
                    if (isPrivate && ps.Count > 0 && ps[0] == 25) _buffer.SetCursorVisible(false);
                    break;

                // Unsupported final bytes (device queries, etc.) are silently ignored.
            }
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
