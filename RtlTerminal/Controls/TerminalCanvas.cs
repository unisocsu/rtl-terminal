using System;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using RtlTerminal.Services;
using RtlTerminal.Terminal;

namespace RtlTerminal.Controls
{
    /// <summary>
    /// Draws a TerminalBuffer directly with DrawingContext - a real character-grid terminal
    /// screen instead of an append-only text control. Handles per-row RTL detection (Hebrew/
    /// Arabic lines are mirrored and right-aligned) and a blinking caret at the buffer's cursor
    /// position.
    /// </summary>
    public sealed class TerminalCanvas : FrameworkElement
    {
        public TerminalBuffer? Buffer { get; private set; }

        public (int Row, int Col)? SelectionAnchor { get; private set; }
        public (int Row, int Col)? SelectionEnd { get; private set; }

        public bool HasSelection => SelectionAnchor is not null && SelectionEnd is not null;

        /// <summary>0 = live (following the shell's current output). Larger values look further
        /// back into scrollback history.</summary>
        public int ScrollOffset { get; private set; }

        public int MaxScrollOffset => Buffer?.ScrollbackCount ?? 0;

        /// <summary>Raised whenever ScrollOffset changes, so the hosting view can keep an
        /// external scrollbar in sync.</summary>
        public event Action? ScrollOffsetChanged;

        private readonly Typeface _typeface = new(new FontFamily("Cascadia Mono, Consolas, Courier New"),
            FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        private readonly Typeface _boldTypeface = new(new FontFamily("Cascadia Mono, Consolas, Courier New"),
            FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        public double FontSize { get; set; } = 14;
        public double CellWidth { get; private set; }
        public double CellHeight { get; private set; }

        private readonly DispatcherTimer _caretTimer;
        private bool _caretBlinkOn = true;

        private static readonly SolidColorBrush DefaultForeground = new(Color.FromRgb(0xE8, 0xE8, 0xE8));
        private static readonly SolidColorBrush DefaultBackground = new(Color.FromRgb(0x0C, 0x0C, 0x0C));

        public TerminalCanvas()
        {
            SnapsToDevicePixels = true;
            MeasureCellSize();

            _caretTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
            _caretTimer.Tick += (_, _) =>
            {
                _caretBlinkOn = !_caretBlinkOn;
                InvalidateVisual();
            };
            _caretTimer.Start();
        }

        public void AttachBuffer(TerminalBuffer buffer)
        {
            if (Buffer is not null)
            {
                Buffer.Changed -= OnBufferChanged;
                Buffer.ScrolledOut -= OnScrolledOut;
            }
            Buffer = buffer;
            Buffer.Changed += OnBufferChanged;
            Buffer.ScrolledOut += OnScrolledOut;
            ScrollOffset = 0;
            InvalidateVisual();
        }

        private void OnScrolledOut(int linesScrolled)
        {
            // Already on the UI thread: ScrollUp is only ever called from VtParser.Feed, which
            // is itself dispatched onto the UI thread (see TerminalView.OnOutputReceived).
            if (ScrollOffset > 0)
            {
                SetScrollOffset(ScrollOffset + linesScrolled); // also raises ScrollOffsetChanged
            }
            else
            {
                // Offset itself didn't move, but ScrollbackCount (and so MaxScrollOffset) grew -
                // let the hosting view refresh its scrollbar's range even though the value is
                // unchanged.
                ScrollOffsetChanged?.Invoke();
            }
        }

        /// <summary>Moves the viewport. 0 = live/bottom. Clamped to available scrollback.</summary>
        public void SetScrollOffset(int offset)
        {
            if (Buffer is null) return;
            int clamped = Math.Clamp(offset, 0, Buffer.ScrollbackCount);
            if (clamped == ScrollOffset) return;
            ScrollOffset = clamped;
            ScrollOffsetChanged?.Invoke();
            InvalidateVisual();
        }

        /// <summary>Returns the cell that should be shown at the given screen row/col, accounting
        /// for ScrollOffset - transparently reading from scrollback history or the live grid.</summary>
        private Cell GetDisplayCell(int screenRow, int col)
        {
            if (Buffer is null) return Cell.Blank;

            int totalLines = Buffer.ScrollbackCount + Buffer.Rows;
            int topLineIndex = totalLines - Buffer.Rows - ScrollOffset;
            int lineIndex = topLineIndex + screenRow;

            if (lineIndex < 0) return Cell.Blank;

            if (lineIndex < Buffer.ScrollbackCount)
            {
                Cell[] line = Buffer.GetScrollbackLine(lineIndex);
                return col < line.Length ? line[col] : Cell.Blank;
            }

            int liveRow = lineIndex - Buffer.ScrollbackCount;
            if (liveRow < 0 || liveRow >= Buffer.Rows) return Cell.Blank;
            return Buffer.Grid[liveRow, col];
        }

        private void OnBufferChanged()
        {
            Dispatcher.BeginInvoke(new Action(InvalidateVisual), DispatcherPriority.Render);
        }

        private void MeasureCellSize()
        {
            var ft = new FormattedText("M", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, _typeface, FontSize, Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            CellWidth = ft.WidthIncludingTrailingWhitespace;
            CellHeight = ft.Height;
        }

        /// <summary>How many whole columns/rows fit in the given pixel size, given current font metrics.</summary>
        public (int columns, int rows) ComputeGridSize(double widthPx, double heightPx)
        {
            int cols = Math.Max(1, (int)(widthPx / CellWidth));
            int rows = Math.Max(1, (int)(heightPx / CellHeight));
            return (cols, rows);
        }

        /// <summary>Converts a pixel position within the canvas to a (row, col) cell, clamped to the buffer bounds.
        /// Accounts for run-based RTL layout (see RenderRow) so selection lines up with what's drawn.</summary>
        public (int Row, int Col) HitTestCell(Point p)
        {
            if (Buffer is null) return (0, 0);
            int visualCol = (int)(p.X / CellWidth);
            int row = (int)(p.Y / CellHeight);
            visualCol = Math.Clamp(visualCol, 0, Buffer.Columns - 1);
            row = Math.Clamp(row, 0, Buffer.Rows - 1);

            string rowText = BuildRowText(row);
            bool baseRtl = RtlTextHelper.DetectFlowDirection(rowText.TrimEnd()) == FlowDirection.RightToLeft;
            int[] logicalToVisual = RtlTextHelper.BuildLogicalToVisualMap(rowText, baseRtl);

            // Invert the map to go from the clicked visual column back to the logical column.
            int logicalCol = visualCol;
            for (int i = 0; i < logicalToVisual.Length; i++)
            {
                if (logicalToVisual[i] == visualCol) { logicalCol = i; break; }
            }
            return (row, logicalCol);
        }

        private string BuildRowText(int row)
        {
            if (Buffer is null) return string.Empty;
            var sb = new StringBuilder(Buffer.Columns);
            for (int x = 0; x < Buffer.Columns; x++)
                sb.Append(GetDisplayCell(row, x).Ch);
            return sb.ToString();
        }

        public void StartSelection((int Row, int Col) cell)
        {
            SelectionAnchor = cell;
            SelectionEnd = cell;
            InvalidateVisual();
        }

        public void UpdateSelection((int Row, int Col) cell)
        {
            if (SelectionAnchor is null) return;
            SelectionEnd = cell;
            InvalidateVisual();
        }

        public void ClearSelection()
        {
            if (SelectionAnchor is null && SelectionEnd is null) return;
            SelectionAnchor = null;
            SelectionEnd = null;
            InvalidateVisual();
        }

        /// <summary>Extracts the plain text currently covered by the selection, in reading order.</summary>
        public string GetSelectedText()
        {
            if (Buffer is null || SelectionAnchor is null || SelectionEnd is null) return string.Empty;

            var (startRow, startCol, endRow, endCol) = NormalizeSelection();

            var sb = new StringBuilder();
            for (int row = startRow; row <= endRow; row++)
            {
                int fromCol = row == startRow ? startCol : 0;
                int toCol = row == endRow ? endCol : Buffer.Columns - 1;

                var lineBuilder = new StringBuilder();
                for (int col = fromCol; col <= toCol && col < Buffer.Columns; col++)
                    lineBuilder.Append(GetDisplayCell(row, col).Ch);

                sb.Append(lineBuilder.ToString().TrimEnd());
                if (row < endRow) sb.Append(Environment.NewLine);
            }
            return sb.ToString();
        }

        private (int startRow, int startCol, int endRow, int endCol) NormalizeSelection()
        {
            var a = SelectionAnchor!.Value;
            var b = SelectionEnd!.Value;
            if (a.Row < b.Row || (a.Row == b.Row && a.Col <= b.Col))
                return (a.Row, a.Col, b.Row, b.Col);
            return (b.Row, b.Col, a.Row, a.Col);
        }

        private bool IsCellSelected(int row, int col)
        {
            if (!HasSelection) return false;
            var (startRow, startCol, endRow, endCol) = NormalizeSelection();
            if (row < startRow || row > endRow) return false;
            if (row == startRow && col < startCol) return false;
            if (row == endRow && col > endCol) return false;
            return true;
        }
        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(DefaultBackground, null, new Rect(0, 0, ActualWidth, ActualHeight));

            if (Buffer is null) return;

            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            for (int y = 0; y < Buffer.Rows; y++)
            {
                RenderRow(dc, y, dpi);
            }

            if (ScrollOffset == 0 && Buffer.CursorVisible && _caretBlinkOn)
            {
                double cx = Buffer.CursorX * CellWidth;
                double cy = Buffer.CursorY * CellHeight;
                var caretBrush = new SolidColorBrush(Color.FromArgb(120, 232, 232, 232));
                dc.DrawRectangle(caretBrush, null, new Rect(cx, cy, CellWidth, CellHeight));
            }
        }

        private void RenderRow(DrawingContext dc, int row, double dpi)
        {
            if (Buffer is null) return;

            string lineText = BuildRowText(row);
            bool baseRtl = RtlTextHelper.DetectFlowDirection(lineText.TrimEnd()) == FlowDirection.RightToLeft;
            int[] logicalToVisual = RtlTextHelper.BuildLogicalToVisualMap(lineText, baseRtl);

            // Draw cell-by-cell so each cell keeps its own color/bold/reverse attributes.
            // logicalToVisual reorders directional *runs* as blocks (see RtlTextHelper): an
            // embedded LTR run (e.g. an English word inside a Hebrew line) keeps its internal
            // left-to-right character order and only moves as a block, while RTL runs are
            // mirrored internally - unlike a naive full-line mirror, which would also reverse
            // the letters within the English word.
            for (int x = 0; x < Buffer.Columns; x++)
            {
                Cell cell = GetDisplayCell(row, x);
                if (cell.Ch == '\0') cell.Ch = ' ';

                int visualX = logicalToVisual[x];

                Brush fg = ResolveBrush(cell.Fg, DefaultForeground);
                Brush? bg = cell.Bg == AnsiColor.Default ? null : ResolveBrush(cell.Bg, DefaultBackground);

                double px = visualX * CellWidth;
                double py = row * CellHeight;

                if (bg is not null)
                    dc.DrawRectangle(bg, null, new Rect(px, py, CellWidth, CellHeight));

                if (IsCellSelected(row, x))
                {
                    var selectionBrush = new SolidColorBrush(Color.FromArgb(90, 90, 150, 220));
                    dc.DrawRectangle(selectionBrush, null, new Rect(px, py, CellWidth, CellHeight));
                }

                if (cell.Ch != ' ')
                {
                    var typeface = cell.Bold ? _boldTypeface : _typeface;
                    var ft = new FormattedText(
                        cell.Ch.ToString(),
                        System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, // individual glyph, direction doesn't matter
                        typeface, FontSize, fg, dpi);

                    if (cell.Underline)
                        ft.SetTextDecorations(TextDecorations.Underline);

                    dc.DrawText(ft, new Point(px, py));
                }
            }
        }

        private static Brush ResolveBrush(AnsiColor color, Brush fallback)
        {
            return color switch
            {
                AnsiColor.Default => fallback,
                AnsiColor.Black => Brushes.Black,
                AnsiColor.Red => Brushes.Firebrick,
                AnsiColor.Green => Brushes.ForestGreen,
                AnsiColor.Yellow => Brushes.Goldenrod,
                AnsiColor.Blue => Brushes.RoyalBlue,
                AnsiColor.Magenta => Brushes.MediumOrchid,
                AnsiColor.Cyan => Brushes.DarkCyan,
                AnsiColor.White => Brushes.WhiteSmoke,
                AnsiColor.BrightBlack => Brushes.Gray,
                AnsiColor.BrightRed => Brushes.Red,
                AnsiColor.BrightGreen => Brushes.LimeGreen,
                AnsiColor.BrightYellow => Brushes.Yellow,
                AnsiColor.BrightBlue => Brushes.DodgerBlue,
                AnsiColor.BrightMagenta => Brushes.Orchid,
                AnsiColor.BrightCyan => Brushes.Cyan,
                AnsiColor.BrightWhite => Brushes.White,
                _ => fallback
            };
        }
    }
}
