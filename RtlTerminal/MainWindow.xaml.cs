using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using RtlTerminal.Controls;
using RtlTerminal.Models;
using RtlTerminal.Services;

namespace RtlTerminal
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<TerminalTab> Tabs { get; } = new();

        private TerminalTab? _activeTab;
        private readonly System.Collections.Generic.Dictionary<Guid, TerminalView> _views = new();

        public MainWindow()
        {
            InitializeComponent();
            TabStripItems.ItemsSource = Tabs;

            InputBindings.Add(new KeyBinding(
                new RelayCommand(_ => CreateTab(defaultShell: true)), Key.T, ModifierKeys.Control));
            InputBindings.Add(new KeyBinding(
                new RelayCommand(_ => CloseActiveTab()), Key.W, ModifierKeys.Control));

            Loaded += (_, _) =>
            {
                CreateTab(defaultShell: true);
                // PowerShell 7 (pwsh.exe) isn't always installed - disable that menu entry
                // instead of offering a shell that would just fail to launch.
                Pwsh7MenuItem.IsEnabled = ShellPathResolver.TryResolvePwsh() is not null;
            };
            Closed += (_, _) => CleanupAll();
        }

        private void NewTabButton_Click(object sender, RoutedEventArgs e) => CreateTab(defaultShell: true);

        private void NewTabMenuButton_Click(object sender, RoutedEventArgs e)
        {
            ShellMenu.PlacementTarget = NewTabMenuButton;
            ShellMenu.IsOpen = true;
        }

        private void ShellMenuItem_CommandPrompt(object sender, RoutedEventArgs e)
            => CreateTabWithResolver(ShellPathResolver.ResolveCmd, "Command Prompt");

        private void ShellMenuItem_WindowsPowerShell(object sender, RoutedEventArgs e)
            => CreateTabWithResolver(ShellPathResolver.ResolvePowerShell, "Windows PowerShell");

        private void ShellMenuItem_Pwsh7(object sender, RoutedEventArgs e)
        {
            string? path = ShellPathResolver.TryResolvePwsh();
            if (path is null)
            {
                MessageBox.Show("PowerShell 7 (pwsh.exe) לא נמצא במערכת הזו.", "לא זמין",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            CreateTab(path, "PowerShell 7");
        }

        private void CreateTabWithResolver(Func<string> resolve, string title)
        {
            string shellPath;
            try
            {
                shellPath = resolve();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"לא ניתן היה לאתר את נתיב ה-shell:\n{ex.Message}", "שגיאה",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            CreateTab(shellPath, title);
        }

        /// <summary>Default entry point (Ctrl+T, the "+" button): always cmd.exe.</summary>
        private void CreateTab(bool defaultShell)
        {
            if (!defaultShell) return;
            CreateTabWithResolver(ShellPathResolver.ResolveCmd, "Command Prompt");
        }

        private void CreateTab(string shellPath, string title)
        {
            var tab = new TerminalTab(shellPath, title: $"{title} {Tabs.Count + 1}");
            try
            {
                tab.Start(columns: 120, rows: 30);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"לא ניתן היה להפעיל את ה-shell:\n{ex.Message}", "שגיאה",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                tab.Dispose();
                return;
            }

            var view = new TerminalView();
            view.Attach(tab, tab.Buffer);
            _views[tab.Id] = view;

            Tabs.Add(tab);
            ActivateTab(tab);
        }

        private void ActivateTab(TerminalTab tab)
        {
            _activeTab = tab;
            if (_views.TryGetValue(tab.Id, out var view))
            {
                TerminalHost.Child = view;
                view.Focus();
            }
        }

        private void TabHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is TerminalTab tab)
                ActivateTab(tab);
        }

        private void CloseTabButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is TerminalTab tab)
                CloseTab(tab);
        }

        private void CloseActiveTab()
        {
            if (_activeTab is not null)
                CloseTab(_activeTab);
        }

        private void CloseTab(TerminalTab tab)
        {
            if (_views.TryGetValue(tab.Id, out var view))
            {
                view.Detach();
                _views.Remove(tab.Id);
            }

            int index = Tabs.IndexOf(tab);
            Tabs.Remove(tab);
            tab.Dispose();

            if (Tabs.Count == 0)
            {
                CreateTab(defaultShell: true);
                return;
            }

            if (ReferenceEquals(_activeTab, tab))
            {
                int nextIndex = Math.Min(index, Tabs.Count - 1);
                ActivateTab(Tabs[nextIndex]);
            }
        }

        private void CleanupAll()
        {
            foreach (var tab in Tabs.ToList())
            {
                if (_views.TryGetValue(tab.Id, out var view)) view.Detach();
                tab.Dispose();
            }
        }
    }

    /// <summary>Minimal ICommand for keyboard shortcuts, to avoid pulling in a full MVVM framework.</summary>
    internal sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        public RelayCommand(Action<object?> execute) => _execute = execute;
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _execute(parameter);
        public event EventHandler? CanExecuteChanged { add { } remove { } }
    }
}
