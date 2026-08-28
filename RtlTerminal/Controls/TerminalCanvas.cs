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
                double cx = Buffer.CursorX * CellWidth;
                double cy = Buffer.CursorY * CellHeight;
                var caretBrush = new SolidColorBrush(Color.FromArgb(120, 232, 232, 232));
                dc.DrawRectangle(caretBrush, null, new Rect(cx, cy, CellWidth, CellHeight));
            }
        }

        private void RenderRow(DrawingContext dc, int row, double dpi)
        {
            if (Buffer is null) return;

            var rowText = new StringBuilder(Buffer.Columns);
            for (int x = 0; x < Buffer.Columns; x++)
                rowText.Append(Buffer.Grid[row, x].Ch);

            string lineText = rowText.ToString();
            FlowDirection direction = RtlTextHelper.DetectFlowDirection(lineText.TrimEnd());
            bool rtl = direction == FlowDirection.RightToLeft;

            // Draw cell-by-cell so each cell keeps its own color/bold/reverse attributes.
            // For RTL rows, mirror the column order so the visual result reads right-to-left
            // while each character stays upright (real terminal-style bidi approximation).
            for (int x = 0; x < Buffer.Columns; x++)
            {
                Cell cell = Buffer.Grid[row, x];
                if (cell.Ch == '\0') cell.Ch = ' ';

                int visualX = rtl ? (Buffer.Columns - 1 - x) : x;

                Brush fg = ResolveBrush(cell.Fg, DefaultForeground);
                Brush? bg = cell.Bg == AnsiColor.Default ? null : ResolveBrush(cell.Bg, DefaultBackground);

                double px = visualX * CellWidth;
                double py = row * CellHeight;

                if (bg is not null)
                    dc.DrawRectangle(bg, null, new Rect(px, py, CellWidth, CellHeight));

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
