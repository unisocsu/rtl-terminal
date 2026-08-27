using System;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using RtlTerminal.Models;
using RtlTerminal.Services;

namespace RtlTerminal.Controls
{
    /// <summary>
    /// Renders one terminal tab: shows ConPTY output (with per-line RTL/LTR auto-detection)
    /// and forwards raw keystrokes to the shell's stdin, so the shell itself (cmd.exe,
    /// PowerShell, ...) drives the actual interactive behavior - this control is just the
    /// screen + keyboard.
    /// </summary>
    public partial class TerminalView : UserControl
    {
        private TerminalTab? _tab;
        private readonly StringBuilder _pendingLine = new();

        public TerminalView()
        {
            InitializeComponent();
            Loaded += (_, _) => Focus();
        }

        /// <summary>Attaches this view to a running tab's session. Call once, after Session.Start().</summary>
        public void Attach(TerminalTab tab)
        {
            _tab = tab;
            _tab.Session.OutputReceived += OnOutputReceived;
            _tab.Session.ProcessExited += OnProcessExited;
        }

        public void Detach()
        {
            if (_tab is null) return;
            _tab.Session.OutputReceived -= OnOutputReceived;
            _tab.Session.ProcessExited -= OnProcessExited;
        }

        // ---- output (shell -> screen) --------------------------------------------------

        private void OnOutputReceived(byte[] buffer, int count)
        {
            string chunk = Encoding.UTF8.GetString(buffer, 0, count);

            Dispatcher.BeginInvoke(new Action(() => AppendChunk(chunk)));
        }

        private void AppendChunk(string rawChunk)
        {
            string clean = AnsiSequenceStripper.Strip(rawChunk);
            _pendingLine.Append(clean);

            string combined = _pendingLine.ToString();
            string[] parts = combined.Split('\n');

            // Everything except the last part is a complete line.
            for (int i = 0; i < parts.Length - 1; i++)
            {
                AppendLine(parts[i]);
            }

            // Keep the incomplete trailing part (no newline yet, e.g. an active prompt) buffered.
            _pendingLine.Clear();
            _pendingLine.Append(parts[^1]);

            // Also reflect the in-progress prompt line live, without committing it as a final paragraph.
            RefreshLivePromptLine(parts[^1]);

            OutputBox.ScrollToEnd();
        }

        private Paragraph? _livePromptParagraph;

        private void AppendLine(string line)
        {
            // Remove the live (uncommitted) prompt paragraph before adding the finalized line.
            if (_livePromptParagraph is not null)
            {
                OutputBox.Document.Blocks.Remove(_livePromptParagraph);
                _livePromptParagraph = null;
            }

            var paragraph = new Paragraph(new Run(line))
            {
                Margin = new Thickness(0),
                FlowDirection = RtlTextHelper.DetectFlowDirection(line)
            };
            OutputBox.Document.Blocks.Add(paragraph);
        }

        private void RefreshLivePromptLine(string line)
        {
            if (_livePromptParagraph is not null)
                OutputBox.Document.Blocks.Remove(_livePromptParagraph);

            if (string.IsNullOrEmpty(line))
            {
                _livePromptParagraph = null;
                return;
            }

            _livePromptParagraph = new Paragraph(new Run(line))
            {
                Margin = new Thickness(0),
                FlowDirection = RtlTextHelper.DetectFlowDirection(line)
            };
            OutputBox.Document.Blocks.Add(_livePromptParagraph);

            // Move caret to end so the view keeps following the live line.
            OutputBox.CaretPosition = OutputBox.Document.ContentEnd;
        }

        private void OnProcessExited(int exitCode)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AppendLine($"[התהליך הסתיים, קוד יציאה: {exitCode}]");
            }));
        }

        // ---- input (keyboard -> shell stdin) --------------------------------------------

        private void OutputBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            // Every printable character typed goes straight to the shell; the shell's own
            // echo (coming back through OutputReceived) is what actually renders it on screen,
            // so we must NOT let the RichTextBox insert it itself.
            if (_tab is not null && !string.IsNullOrEmpty(e.Text))
            {
                _tab.Session.Write(e.Text);
            }
            e.Handled = true;
        }

        private void OutputBox_PreviewCanExecute(object sender, CanExecuteRoutedEventArgs e)
        {
            // Block built-in editing commands (paste/cut handled manually below, undo/redo disabled)
            // so the RichTextBox never mutates its own document except through our code.
            if (e.Command == ApplicationCommands.Undo || e.Command == ApplicationCommands.Redo)
            {
                e.CanExecute = false;
                e.Handled = true;
            }
        }

        private void OutputBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_tab is null) return;

            switch (e.Key)
            {
                case Key.Enter:
                    _tab.Session.Write("\r");
                    e.Handled = true;
                    return;

                case Key.Back when Keyboard.Modifiers == ModifierKeys.Control:
                    // Ctrl+Backspace = delete word left. Most readline-style input (bash, PowerShell
                    // PSReadLine, Node's readline used by CLIs like Claude Code) maps this to
                    // ESC + DEL (Alt+Backspace in xterm terms), NOT a second plain DEL.
                    _tab.Session.Write("\u001b\x7f");
                    e.Handled = true;
                    return;

                case Key.Back:
                    _tab.Session.Write("\x7f"); // DEL - most shells treat this as backspace under ConPTY
                    e.Handled = true;
                    return;

                case Key.Space:
                    // Handled explicitly rather than relying on PreviewTextInput: WPF's TextInput
                    // pipeline for RichTextBox does not reliably raise TextInput for the space
                    // character in all focus/composition states, which was silently swallowing
                    // spaces before they ever reached the shell.
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

                case Key.Escape:
                    _tab.Session.Write("\u001b");
                    e.Handled = true;
                    return;

                case Key.C when Keyboard.Modifiers == ModifierKeys.Control:
                    _tab.Session.Write("\x03"); // Ctrl+C -> SIGINT-equivalent
                    e.Handled = true;
                    return;

                case Key.V when Keyboard.Modifiers == ModifierKeys.Control:
                    if (Clipboard.ContainsText())
                        _tab.Session.Write(Clipboard.GetText());
                    e.Handled = true;
                    return;
            }
        }

        /// <summary>Call when the hosting layout changes size, to resize the underlying ConPTY buffer.</summary>
        public void NotifyResize(short columns, short rows)
        {
            _tab?.Session.Resize(columns, rows);
        }
    }
}
