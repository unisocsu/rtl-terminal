using System;
using System.Windows;
using System.Windows.Threading;

namespace RtlTerminal
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Catch unhandled exceptions so a ConPTY / P-Invoke failure
            // doesn't just silently kill the process without a message.
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show(
                $"שגיאה לא צפויה:\n{e.Exception.Message}\n\n{e.Exception.StackTrace}",
                "RTL Terminal - שגיאה",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                MessageBox.Show(
                    $"שגיאה קריטית:\n{ex.Message}",
                    "RTL Terminal - שגיאה קריטית",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
