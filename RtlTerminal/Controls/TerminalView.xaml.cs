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
            Canvas.ScrollOffsetChanged += SyncScrollBarFromCanvas;
        }

        /// <summary>Attaches this view to a running tab's session. Call once, after Session.Start().</summary>
        public void Attach(TerminalTab tab, TerminalBuffer buffer)
        {
            _tab = tab;
            _parser = new VtParser(buffer);
            _parser.ActiveBufferChanged += OnActiveBufferChanged;
            Canvas.AttachBuffer(buffer);
            SyncScrollBarFromCanvas();
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
            SyncScrollBarFromCanvas();
        }

        // ---- scrolling -----------------------------------------------------------------

        private bool _syncingScrollBar;

        private void SyncScrollBarFromCanvas()
        {
            // ScrollBar convention: Value=0 is the top (oldest), Value=Maximum is the bottom
            // (live) - the opposite sense from Canvas.ScrollOffset (0 = live). Guard against
            // feedback: setting VScrollBar.Value below fires Scroll, which would otherwise
            // call back into SetScrollOffset for a change we already made ourselves.
            _syncingScrollBar = true;
            VScrollBar.Maximum = Canvas.MaxScrollOffset;
            VScrollBar.ViewportSize = Math.Max(1, Canvas.MaxScrollOffset / 10.0);
            VScrollBar.Value = Canvas.MaxScrollOffset - Canvas.ScrollOffset;
            _syncingScrollBar = false;
        }

        private void VScrollBar_Scroll(object sender, System.Windows.Controls.Primitives.ScrollEventArgs e)
        {
            if (_syncingScrollBar) return;
            Canvas.SetScrollOffset((int)(VScrollBar.Maximum - VScrollBar.Value));
        }

        private double _wheelAccumulator;

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            // Was 3 lines per standard 120-delta wheel notch - felt too slow. Bumped to 8.
            // Accumulating fractional notches (rather than truncating via integer division)
            // also makes precision trackpads, which often send deltas smaller than 120, scroll
            // smoothly instead of needing several flicks before anything visibly moves.
            const double linesPerStandardNotch = 8;
            _wheelAccumulator += e.Delta / 120.0 * linesPerStandardNotch;
            int lines = (int)_wheelAccumulator;
            _wheelAccumulator -= lines;

            if (lines != 0) Canvas.SetScrollOffset(Canvas.ScrollOffset + lines);
            e.Handled = true;
            base.OnMouseWheel(e);
        }

        /// <summary>Any keystroke jumps the view back to live output, matching how real
        /// terminals behave - you shouldn't be scrolled into history while typing blind.</summary>
        private void JumpToLiveOnInput()
        {
            if (Canvas.ScrollOffset != 0) Canvas.SetScrollOffset(0);
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
            JumpToLiveOnInput();
            if (_tab is not null && !string.IsNullOrEmpty(e.Text))
            {
                _tab.Session.Write(e.Text);
            }
            e.Handled = true;
            base.OnPreviewTextInput(e);
        }

        // ---- Win32-Input-Mode -----------------------------------------------------------
        //
        // Confirmed via debug log: conhost sends "CSI ? 9001 h" at startup, which requests
        // Win32-Input-Mode - a Windows/ConPTY-specific protocol where non-printable/special
        // keys (Backspace, Enter, arrows, Tab, Ctrl+letter, ...) are expected as a structured
        // sequence carrying the actual Win32 virtual-key code and control-key-state, rather
        // than a plain control byte. Plain printable characters (letters/digits/symbols via
        // OnPreviewTextInput above) are unambiguous either way and don't need this - which
        // matches what was observed: typed letters worked fine, but Backspace (a control key)
        // did not. Format (documented by Microsoft): CSI Vk;Sc;Uc;Kd;Cs;Rc _
        // (Vk=virtual key code, Sc=scan code, Uc=unicode char code, Kd=key down 1/0,
        // Cs=control-key-state bitmask, Rc=repeat count; terminated by a literal '_').

        private const int LEFT_CTRL_PRESSED = 0x0008;

        private const int VK_BACK = 0x08;
        private const int VK_TAB = 0x09;
        private const int VK_RETURN = 0x0D;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_SPACE = 0x20;
        private const int VK_END = 0x23;
        private const int VK_HOME = 0x24;
        private const int VK_LEFT = 0x25;
        private const int VK_UP = 0x26;
        private const int VK_RIGHT = 0x27;
        private const int VK_DOWN = 0x28;
        private const int VK_DELETE = 0x2E;
        private const int VK_C = 0x43;
        private const int VK_W = 0x57;

        private static string EncodeWin32Key(int vk, int unicodeChar, bool keyDown, int controlKeyState = 0)
            => $"\u001b[{vk};0;{unicodeChar};{(keyDown ? 1 : 0)};{controlKeyState};1_";

        /// <summary>Sends a full key-down + key-up pair, matching how a real keyboard event
        /// arrives - some Win32-Input-Mode consumers key off the up event too.</summary>
        private void SendWin32Key(int vk, int unicodeChar, int controlKeyState = 0)
        {
            if (_tab is null) return;
            _tab.Session.Write(EncodeWin32Key(vk, unicodeChar, keyDown: true, controlKeyState)
                              + EncodeWin32Key(vk, unicodeChar, keyDown: false, controlKeyState));
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (_tab is null) { base.OnPreviewKeyDown(e); return; }
            JumpToLiveOnInput();

            switch (e.Key)
            {
                case Key.Enter:
                    SendWin32Key(VK_RETURN, '\r');
                    e.Handled = true;
                    return;

                case Key.Back when Keyboard.Modifiers == ModifierKeys.Control:
                    // Ctrl+Backspace = delete word left (Ctrl+W is bash/zsh/PSReadLine/Node
                    // readline's "unix-word-rubout"; encoded here as Ctrl+W with LEFT_CTRL_PRESSED
                    // so conhost's Win32-Input-Mode parser sees the actual control-key state).
                    SendWin32Key(VK_W, 0x17, LEFT_CTRL_PRESSED);
                    e.Handled = true;
                    return;

                case Key.Back:
                    SendWin32Key(VK_BACK, 0x08);
                    e.Handled = true;
                    return;

                case Key.Space:
                    SendWin32Key(VK_SPACE, ' ');
                    e.Handled = true;
                    return;

                case Key.Tab:
                    SendWin32Key(VK_TAB, '\t');
                    e.Handled = true;
                    return;

                case Key.Up:
                    SendWin32Key(VK_UP, 0);
                    e.Handled = true;
                    return;
                case Key.Down:
                    SendWin32Key(VK_DOWN, 0);
                    e.Handled = true;
                    return;
                case Key.Right:
                    SendWin32Key(VK_RIGHT, 0);
                    e.Handled = true;
                    return;
                case Key.Left:
                    SendWin32Key(VK_LEFT, 0);
                    e.Handled = true;
                    return;

                case Key.Delete:
                    SendWin32Key(VK_DELETE, 0);
                    e.Handled = true;
                    return;
                case Key.Home:
                    SendWin32Key(VK_HOME, 0);
                    e.Handled = true;
                    return;
                case Key.End:
                    SendWin32Key(VK_END, 0);
                    e.Handled = true;
                    return;

                case Key.Escape:
                    SendWin32Key(VK_ESCAPE, 0x1B);
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
                        SendWin32Key(VK_C, 0x03, LEFT_CTRL_PRESSED); // Ctrl+C -> SIGINT-equivalent
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
