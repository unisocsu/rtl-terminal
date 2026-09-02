using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RtlTerminal.Models;
using RtlTerminal.Terminal;

namespace RtlTerminal.Controls
{
    /// <summary>
    /// Hosts one terminal tab's screen: feeds ConPTY output through a VtParser into a
    /// TerminalBuffer, which TerminalCanvas draws as a real character grid, and forwards
    /// raw keystrokes to the shell's stdin.
    /// </summary>
    public partial class TerminalView : UserControl
    {
        private TerminalTab? _tab;
        private VtParser? _parser;
        private readonly Decoder _utf8Decoder = Encoding.UTF8.GetDecoder();

        public TerminalView()
        {
            InitializeComponent();
            Loaded += (_, _) => Focus();
        }

        /// <summary>Attaches this view to a running tab's session. Call once, after Session.Start().</summary>
        public void Attach(TerminalTab tab, TerminalBuffer buffer)
        {
            _tab = tab;
            _parser = new VtParser(buffer);
            _parser.ActiveBufferChanged += OnActiveBufferChanged;
            Canvas.AttachBuffer(buffer);
            _tab.Session.OutputReceived += OnOutputReceived;
            _tab.Session.ProcessExited += OnProcessExited;
        }

        public void Detach()
        {
            if (_tab is null) return;
            _tab.Session.OutputReceived -= OnOutputReceived;
            _tab.Session.ProcessExited -= OnProcessExited;
            if (_parser is not null) _parser.ActiveBufferChanged -= OnActiveBufferChanged;
        }

        private void OnActiveBufferChanged(TerminalBuffer newActive)
        {
            // Already runs on the UI thread (only ever raised from within Feed(), which is
            // itself dispatched onto the UI thread - see OnOutputReceived).
            Canvas.AttachBuffer(newActive);
        }

        // ---- output (shell -> screen) --------------------------------------------------

        private void OnOutputReceived(byte[] buffer, int count)
        {
            // Decode incrementally: a multi-byte UTF-8 character can be split across two
            // separate ConPTY read chunks, and a persistent Decoder correctly carries the
            // partial bytes over to the next call instead of corrupting the character.
            int maxChars = _utf8Decoder.GetCharCount(buffer, 0, count);
            char[] chars = new char[maxChars];
            int charCount = _utf8Decoder.GetChars(buffer, 0, count, chars, 0);
            string text = new string(chars, 0, charCount);

            // OutputReceived fires on the ConPTY background read thread. Parsing mutates
            // TerminalBuffer's grid directly, and TerminalCanvas reads that same grid on the UI
            // thread during rendering - doing both without marshaling onto one thread is a real
            // race (can produce torn/half-updated frames, which looks exactly like random
            // corruption). Dispatcher.BeginInvoke calls are processed in the order they're
            // queued, and this loop only ever has one outstanding read at a time, so chunk order
            // is preserved even though each one is now handled asynchronously on the UI thread.
            Dispatcher.BeginInvoke(new Action(() => _parser?.Feed(text)));
        }

        private void OnProcessExited(int exitCode)
        {
            // The buffer itself has no "text log" concept anymore; exit is surfaced via the tab title
            // (see MainWindow) rather than printed into the grid.
        }

        // ---- input (keyboard -> shell stdin) --------------------------------------------

        protected override void OnPreviewTextInput(TextCompositionEventArgs e)
        {
            if (_tab is not null && !string.IsNullOrEmpty(e.Text))
            {
                _tab.Session.Write(e.Text);
            }
            e.Handled = true;
            base.OnPreviewTextInput(e);
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (_tab is null) { base.OnPreviewKeyDown(e); return; }

            switch (e.Key)
            {
                case Key.Enter:
                    _tab.Session.Write("\r");
                    e.Handled = true;
                    return;

                case Key.Back when Keyboard.Modifiers == ModifierKeys.Control:
                    // Ctrl+W (0x17) - standard "unix-word-rubout" binding recognized by bash, zsh,
                    // PSReadLine and Node's readline (used by Claude Code).
                    _tab.Session.Write("\x17");
                    e.Handled = true;
                    return;

                case Key.Back:
                    // BS (0x08): cmd.exe / conhost's native line editor expects the Windows-native
                    // backspace byte, not DEL (0x7f, the Unix/xterm convention).
                    _tab.Session.Write("\b");
                    e.Handled = true;
                    return;

                case Key.Space:
                    _tab.Session.Write(" ");
                    e.Handled = true;
                    return;

                case Key.Tab:
                    _tab.Session.Write("\t");
                    e.Handled = true;
                    return;

                case Key.Up:
                    _tab.Session.Write("\u001b[A");
                    e.Handled = true;
                    return;
                case Key.Down:
                    _tab.Session.Write("\u001b[B");
                    e.Handled = true;
                    return;
                case Key.Right:
                    _tab.Session.Write("\u001b[C");
                    e.Handled = true;
                    return;
                case Key.Left:
                    _tab.Session.Write("\u001b[D");
                    e.Handled = true;
                    return;

                case Key.Delete:
                    _tab.Session.Write("\u001b[3~");
                    e.Handled = true;
                    return;
                case Key.Home:
                    _tab.Session.Write("\u001b[H");
                    e.Handled = true;
                    return;
                case Key.End:
                    _tab.Session.Write("\u001b[F");
                    e.Handled = true;
                    return;

                case Key.Escape:
                    _tab.Session.Write("\u001b");
                    e.Handled = true;
                    return;

                case Key.C when Keyboard.Modifiers == ModifierKeys.Control:
                    if (Canvas.HasSelection)
                    {
                        string selected = Canvas.GetSelectedText();
                        if (!string.IsNullOrEmpty(selected))
                            Clipboard.SetText(selected);
                        Canvas.ClearSelection();
                    }
                    else
                    {
                        _tab.Session.Write("\x03"); // Ctrl+C -> SIGINT-equivalent
                    }
                    e.Handled = true;
                    return;

                case Key.V when Keyboard.Modifiers == ModifierKeys.Control:
                    if (Clipboard.ContainsText())
                        _tab.Session.Write(Clipboard.GetText());
                    e.Handled = true;
                    return;
            }

            base.OnPreviewKeyDown(e);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            Canvas.ClearSelection();
            var cell = Canvas.HitTestCell(e.GetPosition(Canvas));
            Canvas.StartSelection(cell);
            CaptureMouse();
            e.Handled = true;
            base.OnMouseLeftButtonDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && IsMouseCaptured)
            {
                var cell = Canvas.HitTestCell(e.GetPosition(Canvas));
                Canvas.UpdateSelection(cell);
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            if (IsMouseCaptured) ReleaseMouseCapture();

            // A click with no drag (anchor == end) is not a meaningful selection - clear it
            // so it doesn't block normal typing/interrupt behavior.
            if (Canvas.SelectionAnchor == Canvas.SelectionEnd)
                Canvas.ClearSelection();

            base.OnMouseLeftButtonUp(e);
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            // Right-click: standard terminal convenience - copy selection if present,
            // otherwise paste clipboard contents.
            if (Canvas.HasSelection)
            {
                string selected = Canvas.GetSelectedText();
                if (!string.IsNullOrEmpty(selected))
                    Clipboard.SetText(selected);
                Canvas.ClearSelection();
            }
            else if (_tab is not null && Clipboard.ContainsText())
            {
                _tab.Session.Write(Clipboard.GetText());
            }
            e.Handled = true;
            base.OnMouseRightButtonDown(e);
        }

        // ---- resize ------------------------------------------------------------------

        private void TerminalView_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_tab is null || ActualWidth <= 0 || ActualHeight <= 0) return;

            var (cols, rows) = Canvas.ComputeGridSize(ActualWidth, ActualHeight);
            _tab.Session.Resize((short)cols, (short)rows);
            _parser?.Resize(cols, rows);
        }
    }
}
