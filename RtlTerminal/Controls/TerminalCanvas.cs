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
            if (Buffer is not null) Buffer.Changed -= OnBufferChanged;
            Buffer = buffer;
            Buffer.Changed += OnBufferChanged;
            InvalidateVisual();
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
        /// Accounts for RTL row mirroring (see RenderRow) so selection lines up with what's drawn.</summary>
        public (int Row, int Col) HitTestCell(Point p)
        {
            if (Buffer is null) return (0, 0);
            int visualCol = (int)(p.X / CellWidth);
            int row = (int)(p.Y / CellHeight);
            visualCol = Math.Clamp(visualCol, 0, Buffer.Columns - 1);
            row = Math.Clamp(row, 0, Buffer.Rows - 1);

            var (_, visualToLogical) = GetBidiMappings(row);
            int logicalCol = (visualToLogical is not null && visualCol < visualToLogical.Length) ? visualToLogical[visualCol] : visualCol;
            return (row, logicalCol);
        }

        private bool IsRowRtl(int row)
        {
            if (Buffer is null) return false;
            var rowText = new StringBuilder(Buffer.Columns);
            for (int x = 0; x < Buffer.Columns; x++)
                rowText.Append(Buffer.Grid[row, x].Ch);
            return RtlTextHelper.DetectFlowDirection(rowText.ToString().TrimEnd()) == FlowDirection.RightToLeft;
        }

        private (int[] logicalToVisual, int[] visualToLogical) GetBidiMappings(int row)
        {
            if (Buffer is null)
            {
                int[] empty = Array.Empty<int>();
                return (empty, empty);
            }

            int columns = Buffer.Columns;
            Cell[] rowCells = new Cell[columns];
            for (int x = 0; x < columns; x++)
                rowCells[x] = Buffer.Grid[row, x];

            bool isRtlLine = IsRowRtl(row);

            int[] logicalToVisual = new int[columns];
            int[] visualToLogical = new int[columns];

            var runs = new System.Collections.Generic.List<BidiRun>();
            if (columns > 0)
            {
                bool currentIsRtl = RtlTextHelper.IsStrongRtl(rowCells[0].Ch);
                int runStart = 0;
                for (int i = 1; i < columns; i++)
                {
                    bool isRtl = RtlTextHelper.IsStrongRtl(rowCells[i].Ch);
                    if (isRtl != currentIsRtl)
                    {
                        runs.Add(new BidiRun { Start = runStart, Length = i - runStart, IsRtl = currentIsRtl });
                        runStart = i;
                        currentIsRtl = isRtl;
                    }
                }
                runs.Add(new BidiRun { Start = runStart, Length = columns - runStart, IsRtl = currentIsRtl });
            }

            if (isRtlLine)
            {
                int visualCursor = columns - 1;
                foreach (var run in runs)
                {
                    if (run.IsRtl)
                    {
                        for (int i = 0; i < run.Length; i++)
                        {
                            int logicalX = run.Start + i;
                            int visualX = visualCursor - i;
                            logicalToVisual[logicalX] = visualX;
                            visualToLogical[visualX] = logicalX;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < run.Length; i++)
                        {
                            int logicalX = run.Start + i;
                            int visualX = (visualCursor - run.Length + 1) + i;
                            logicalToVisual[logicalX] = visualX;
                            visualToLogical[visualX] = logicalX;
                        }
                    }
                    visualCursor -= run.Length;
                }
            }
            else
            {
                int visualCursor = 0;
                foreach (var run in runs)
                {
                    if (run.IsRtl)
                    {
                        for (int i = 0; i < run.Length; i++)
                        {
                            int logicalX = run.Start + i;
                            int visualX = (visualCursor + run.Length - 1) - i;
                            logicalToVisual[logicalX] = visualX;
                            visualToLogical[visualX] = logicalX;
                        }
                    }
                    else
                    {
                        for (int i = 0; i < run.Length; i++)
                        {
                            int logicalX = run.Start + i;
                            int visualX = visualCursor + i;
                            logicalToVisual[logicalX] = visualX;
                            visualToLogical[visualX] = logicalX;
                        }
                    }
                    visualCursor += run.Length;
                }
            }

            return (logicalToVisual, visualToLogical);
        }

        private struct BidiRun
        {
            public int Start;
            public int Length;
            public bool IsRtl;
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
                    lineBuilder.Append(Buffer.Grid[row, col].Ch);

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

            if (Buffer.CursorVisible && _caretBlinkOn)
            {
                int cy_row = Buffer.CursorY;
                int cx_logical = Buffer.CursorX;
                
                int cx_visual = cx_logical;
                if (cy_row >= 0 && cy_row < Buffer.Rows && cx_logical >= 0 && cx_logical < Buffer.Columns)
                {
                    var (logicalToVisual, _) = GetBidiMappings(cy_row);
                    if (logicalToVisual is not null && cx_logical < logicalToVisual.Length)
                    {
                        cx_visual = logicalToVisual[cx_logical];
                    }
                }

                double cx = cx_visual * CellWidth;
                double cy = cy_row * CellHeight;
                var caretBrush = new SolidColorBrush(Color.FromArgb(120, 232, 232, 232));
                dc.DrawRectangle(caretBrush, null, new Rect(cx, cy, CellWidth, CellHeight));
            }
        }

        private void RenderRow(DrawingContext dc, int row, double dpi)
        {
            if (Buffer is null) return;

            var (logicalToVisual, _) = GetBidiMappings(row);

            // Draw cell-by-cell so each cell keeps its own color/bold/reverse attributes.
            for (int x = 0; x < Buffer.Columns; x++)
            {
                Cell cell = Buffer.Grid[row, x];
                if (cell.Ch == '\0') cell.Ch = ' ';

                int visualX = (logicalToVisual is not null && x < logicalToVisual.Length) ? logicalToVisual[x] : x;

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
