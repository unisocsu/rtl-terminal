using System;
using System.IO;
using Microsoft.Win32;

namespace RtlTerminal.Services
{
    /// <summary>
    /// Resolves the real, full path of a shell executable (cmd.exe, powershell.exe, pwsh.exe, ...)
    /// via the Windows "App Paths" registry mechanism, instead of hardcoding the executable name
    /// and letting the OS search PATH. This respects per-user overrides (HKCU) and falls back to
    /// the machine-wide registration (HKLM), and finally to the System32 folder if the registry
    /// key is missing for some reason.
    /// </summary>
    public static class ShellPathResolver
    {
        private const string AppPathsKeyTemplate =
            @"Software\Microsoft\Windows\CurrentVersion\App Paths\{0}";

        /// <summary>
        /// Resolves the full path for the given executable name (e.g. "cmd.exe").
        /// Throws FileNotFoundException if it cannot be located anywhere.
        /// </summary>
        public static string Resolve(string exeName)
        {
            if (string.IsNullOrWhiteSpace(exeName))
                throw new ArgumentException("exeName is required", nameof(exeName));

            if (!exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                exeName += ".exe";

            string subKey = string.Format(AppPathsKeyTemplate, exeName);

            // 1. Per-user override takes priority.
            string? fromUser = ReadDefaultValue(Registry.CurrentUser, subKey);
            if (IsValidExecutable(fromUser))
                return fromUser!;

            // 2. Machine-wide registration (this is where cmd.exe normally lives).
            string? fromMachine = ReadDefaultValue(Registry.LocalMachine, subKey);
            if (IsValidExecutable(fromMachine))
                return fromMachine!;

            // 3. Fallback: System32 (covers a stock Windows install where the
            //    App Paths key might not exist for some reason).
            string fallback = Path.Combine(Environment.SystemDirectory, exeName);
            if (File.Exists(fallback))
                return fallback;

            // 4. Last resort: WOW64 / Sysnative for 32-bit process on 64-bit OS.
            string sysnative = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Sysnative",
                exeName);
            if (File.Exists(sysnative))
                return sysnative;

            throw new FileNotFoundException(
                $"לא ניתן היה לאתר את הנתיב של '{exeName}' לא ברישום ולא בתיקיות המערכת.");
        }

        /// <summary>Convenience default for cmd.exe.</summary>
        public static string ResolveCmd() => Resolve("cmd.exe");

        /// <summary>Convenience for Windows PowerShell 5.x.</summary>
        public static string ResolvePowerShell() => Resolve("powershell.exe");

        /// <summary>
        /// Attempts to resolve PowerShell 7+ (pwsh.exe). Unlike cmd/powershell, pwsh is not
        /// always registered under App Paths (depends on installer), so this can return null.
        /// </summary>
        public static string? TryResolvePwsh()
        {
            try
            {
                return Resolve("pwsh.exe");
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }

        private static string? ReadDefaultValue(RegistryKey root, string subKeyPath)
        {
            try
            {
                using RegistryKey? key = root.OpenSubKey(subKeyPath);
                if (key?.GetValue(null) is string value && !string.IsNullOrWhiteSpace(value))
                {
                    // The (Default) value sometimes includes surrounding quotes.
                    return value.Trim('"');
                }
            }
            catch (System.Security.SecurityException)
            {
                // No permission to read this key - treat as not found.
            }
            return null;
        }

        private static bool IsValidExecutable(string? path)
            => !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }
}
