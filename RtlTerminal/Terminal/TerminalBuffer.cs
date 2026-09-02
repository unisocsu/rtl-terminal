using System;
using System.Collections.Generic;

namespace RtlTerminal.Terminal
{
    public enum AnsiColor
    {
        Default = -1,
        Black = 0, Red = 1, Green = 2, Yellow = 3, Blue = 4, Magenta = 5, Cyan = 6, White = 7,
        BrightBlack = 8, BrightRed = 9, BrightGreen = 10, BrightYellow = 11,
        BrightBlue = 12, BrightMagenta = 13, BrightCyan = 14, BrightWhite = 15
    }

    public struct Cell
    {
        public char Ch;
        public AnsiColor Fg;
        public AnsiColor Bg;
        public bool Bold;
        public bool Underline;
        public bool Reverse;

        public static Cell Blank => new Cell { Ch = ' ', Fg = AnsiColor.Default, Bg = AnsiColor.Default };
    }

    /// <summary>
    /// A real terminal screen buffer: a fixed grid of cells that VtParser mutates in place
    /// (cursor moves, erase, scroll, write) - the same model every real terminal emulator uses,
    /// as opposed to appending lines of text to a log.
    /// </summary>
    public sealed class TerminalBuffer
    {
        public int Columns { get; private set; }
        public int Rows { get; private set; }
        public Cell[,] Grid { get; private set; }

        public int CursorX { get; private set; }
        public int CursorY { get; private set; }
        public bool CursorVisible { get; private set; } = true;

        // Scrolling region (0-based, inclusive). Defaults to the whole screen.
        private int _scrollTop;
        private int _scrollBottom;

        // Current SGR (graphic rendition) state applied to newly written cells.
        private AnsiColor _curFg = AnsiColor.Default;
        private AnsiColor _curBg = AnsiColor.Default;
        private bool _curBold;
        private bool _curUnderline;
        private bool _curReverse;

        private int _savedCursorX, _savedCursorY;

        public event Action? Changed;

        /// <summary>Raised when whole-screen scrolling pushes lines off the top into scrollback,
        /// with the count of lines pushed. Lets a scrolled-up viewport shift by the same amount
        /// so it keeps showing the same historical content instead of jumping.</summary>
        public event Action<int>? ScrolledOut;

        private const int MaxScrollbackLines = 5000;
        private readonly List<Cell[]> _scrollback = new();

        public int ScrollbackCount => _scrollback.Count;

        /// <summary>0 = oldest retained line. Returns a blank line if the buffer has been resized
        /// to a different width since this line was captured (rare edge case, not preserved).</summary>
        public Cell[] GetScrollbackLine(int index) => _scrollback[index];

        public TerminalBuffer(int columns, int rows)
        {
            Columns = Math.Max(1, columns);
            Rows = Math.Max(1, rows);
            Grid = new Cell[Rows, Columns];
            Clear();
            _scrollTop = 0;
            _scrollBottom = Rows - 1;
        }

        private void Clear()
        {
            for (int y = 0; y < Rows; y++)
                for (int x = 0; x < Columns; x++)
                    Grid[y, x] = Cell.Blank;
        }

        public void Resize(int newColumns, int newRows)
        {
            newColumns = Math.Max(1, newColumns);
            newRows = Math.Max(1, newRows);
            if (newColumns == Columns && newRows == Rows) return;

            var newGrid = new Cell[newRows, newColumns];
            for (int y = 0; y < newRows; y++)
                for (int x = 0; x < newColumns; x++)
                    newGrid[y, x] = Cell.Blank;

            int copyRows = Math.Min(Rows, newRows);
            int copyCols = Math.Min(Columns, newColumns);
            for (int y = 0; y < copyRows; y++)
                for (int x = 0; x < copyCols; x++)
                    newGrid[y, x] = Grid[y, x];

            Grid = newGrid;
            Columns = newColumns;
            Rows = newRows;
            _scrollTop = 0;
            _scrollBottom = Rows - 1;
            CursorX = Math.Min(CursorX, Columns - 1);
            CursorY = Math.Min(CursorY, Rows - 1);
            RaiseChanged();
        }

        // ---- writing -----------------------------------------------------

        public void WriteChar(char c)
        {
            if (CursorX >= Columns)
            {
                CursorX = 0;
                LineFeed();
            }

            Grid[CursorY, CursorX] = new Cell
            {
                Ch = c,
                Fg = _curReverse ? _curBg : _curFg,
                Bg = _curReverse ? _curFg : _curBg,
                Bold = _curBold,
                Underline = _curUnderline,
                Reverse = _curReverse
            };
            CursorX++;
        }

        public void LineFeed()
        {
            if (CursorY == _scrollBottom)
            {
                ScrollUp(1);
            }
            else if (CursorY < Rows - 1)
            {
                CursorY++;
            }
            RaiseChanged();
        }

        public void CarriageReturn()
        {
            CursorX = 0;
            RaiseChanged();
        }

        public void Backspace()
        {
            if (CursorX > 0) CursorX--;
            RaiseChanged();
        }

        public void Tab()
        {
            int next = ((CursorX / 8) + 1) * 8;
            CursorX = Math.Min(next, Columns - 1);
            RaiseChanged();
        }

        // ---- cursor movement ----------------------------------------------

        public void MoveCursorUp(int n) { CursorY = Math.Max(_scrollTop, CursorY - Math.Max(1, n)); RaiseChanged(); }
        public void MoveCursorDown(int n) { CursorY = Math.Min(_scrollBottom, CursorY + Math.Max(1, n)); RaiseChanged(); }
        public void MoveCursorForward(int n) { CursorX = Math.Min(Columns - 1, CursorX + Math.Max(1, n)); RaiseChanged(); }
        public void MoveCursorBack(int n) { CursorX = Math.Max(0, CursorX - Math.Max(1, n)); RaiseChanged(); }

        public void SetCursorPosition(int row1Based, int col1Based)
        {
            CursorY = Math.Clamp(row1Based - 1, 0, Rows - 1);
            CursorX = Math.Clamp(col1Based - 1, 0, Columns - 1);
            RaiseChanged();
        }

        public void SaveCursor() { _savedCursorX = CursorX; _savedCursorY = CursorY; }
        public void RestoreCursor() { CursorX = _savedCursorX; CursorY = _savedCursorY; RaiseChanged(); }

        public void SetCursorVisible(bool visible) { CursorVisible = visible; RaiseChanged(); }

        public void SetScrollRegion(int top1Based, int bottom1Based)
        {
            _scrollTop = Math.Clamp(top1Based - 1, 0, Rows - 1);
            _scrollBottom = Math.Clamp(bottom1Based - 1, _scrollTop, Rows - 1);
            CursorX = 0;
            CursorY = _scrollTop;
        }

        // ---- erase / scroll -------------------------------------------------

        public void ScrollUp(int n)
        {
            // Only capture scrollback when the WHOLE screen scrolls (the common case for a plain
            // shell prompt). An app-defined partial scroll region (e.g. a status bar pinned at
            // the top) is more like internal windowing than something you'd want in history.
            bool capturesScrollback = _scrollTop == 0;

            for (int s = 0; s < n; s++)
            {
                if (capturesScrollback)
                {
                    var line = new Cell[Columns];
                    for (int x = 0; x < Columns; x++) line[x] = Grid[0, x];
                    _scrollback.Add(line);
                    if (_scrollback.Count > MaxScrollbackLines) _scrollback.RemoveAt(0);
                }

                for (int y = _scrollTop; y < _scrollBottom; y++)
                    for (int x = 0; x < Columns; x++)
                        Grid[y, x] = Grid[y + 1, x];

                for (int x = 0; x < Columns; x++)
                    Grid[_scrollBottom, x] = Cell.Blank;
            }

            if (capturesScrollback && n > 0) ScrolledOut?.Invoke(n);
            RaiseChanged();
        }

        /// <summary>mode: 0 = cursor..end, 1 = start..cursor, 2 = whole line</summary>
        public void EraseInLine(int mode)
        {
            switch (mode)
            {
                case 0:
                    for (int x = CursorX; x < Columns; x++) Grid[CursorY, x] = Cell.Blank;
                    break;
                case 1:
                    for (int x = 0; x <= CursorX && x < Columns; x++) Grid[CursorY, x] = Cell.Blank;
                    break;
                case 2:
                    for (int x = 0; x < Columns; x++) Grid[CursorY, x] = Cell.Blank;
                    break;
            }
            RaiseChanged();
        }

        /// <summary>mode: 0 = cursor..end of screen, 1 = start..cursor, 2 = whole screen</summary>
        public void EraseInDisplay(int mode)
        {
            switch (mode)
            {
                case 0:
                    for (int x = CursorX; x < Columns; x++) Grid[CursorY, x] = Cell.Blank;
                    for (int y = CursorY + 1; y < Rows; y++)
                        for (int x = 0; x < Columns; x++) Grid[y, x] = Cell.Blank;
                    break;
                case 1:
                    for (int x = 0; x <= CursorX && x < Columns; x++) Grid[CursorY, x] = Cell.Blank;
                    for (int y = 0; y < CursorY; y++)
                        for (int x = 0; x < Columns; x++) Grid[y, x] = Cell.Blank;
                    break;
                case 2:
                case 3:
                    Clear();
                    break;
            }
            RaiseChanged();
        }

        // ---- SGR (colors / attributes) --------------------------------------

        public void ResetSgr()
        {
            _curFg = AnsiColor.Default;
            _curBg = AnsiColor.Default;
            _curBold = false;
            _curUnderline = false;
            _curReverse = false;
        }

        public void ApplySgr(int code)
        {
            switch (code)
            {
                case 0: ResetSgr(); break;
                case 1: _curBold = true; break;
                case 4: _curUnderline = true; break;
                case 7: _curReverse = true; break;
                case 22: _curBold = false; break;
                case 24: _curUnderline = false; break;
                case 27: _curReverse = false; break;
                case 39: _curFg = AnsiColor.Default; break;
                case 49: _curBg = AnsiColor.Default; break;
                default:
                    if (code >= 30 && code <= 37) _curFg = (AnsiColor)(code - 30);
                    else if (code >= 90 && code <= 97) _curFg = (AnsiColor)(code - 90 + 8);
                    else if (code >= 40 && code <= 47) _curBg = (AnsiColor)(code - 40);
                    else if (code >= 100 && code <= 107) _curBg = (AnsiColor)(code - 100 + 8);
                    break;
            }
        }

        private void RaiseChanged() => Changed?.Invoke();
    }
}
