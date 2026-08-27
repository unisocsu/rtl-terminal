using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using RtlTerminal.Services;

namespace RtlTerminal.Models
{
    /// <summary>
    /// Represents a single terminal tab: its own shell process/ConPTY session and title.
    /// The actual rendered text buffer lives in the paired TerminalView control (Controls/TerminalView),
    /// this model just owns the process lifetime and identity for the tab strip.
    /// </summary>
    public sealed class TerminalTab : INotifyPropertyChanged, IDisposable
    {
        private string _title;

        public Guid Id { get; } = Guid.NewGuid();

        public ConPtySession Session { get; }

        public string ShellPath { get; }

        public string Title
        {
            get => _title;
            set
            {
                if (_title == value) return;
                _title = value;
                OnPropertyChanged();
            }
        }

        private bool _isClosed;
        public bool IsClosed
        {
            get => _isClosed;
            private set { _isClosed = value; OnPropertyChanged(); }
        }

        public TerminalTab(string shellPath, string? title = null)
        {
            ShellPath = shellPath;
            _title = title ?? System.IO.Path.GetFileNameWithoutExtension(shellPath);
            Session = new ConPtySession();
            Session.ProcessExited += _ => IsClosed = true;
        }

        public void Start(short columns, short rows, string? workingDirectory = null)
        {
            Session.Start(ShellPath, arguments: null, columns: columns, rows: rows, workingDirectory: workingDirectory);
        }

        public void Dispose() => Session.Dispose();

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
