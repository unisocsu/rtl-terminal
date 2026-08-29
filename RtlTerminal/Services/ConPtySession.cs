using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace RtlTerminal.Services
{
    /// <summary>
    /// Thin wrapper around the Windows Pseudo Console (ConPTY) API.
    /// Launches a shell process attached to a pseudo console so it behaves like a
    /// real interactive terminal (cursor movement, screen redraws, resize, etc.),
    /// instead of a plain redirected-pipes child process.
    /// </summary>
    public sealed class ConPtySession : IDisposable
    {
        // ---- Win32 interop -------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct COORD
        {
            public short X;
            public short Y;
            public COORD(short x, short y) { X = x; Y = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STARTUPINFO
        {
            public int cb;
            public IntPtr lpReserved;
            public IntPtr lpDesktop;
            public IntPtr lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public int dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            public int bInheritHandle;
        }

        private const int EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
        private const int CREATE_UNICODE_ENVIRONMENT = 0x00000400;
        private static readonly IntPtr PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = (IntPtr)0x00020016;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

        [DllImport("kernel32.dll")]
        private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

        [DllImport("kernel32.dll")]
        private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

        [DllImport("kernel32.dll")]
        private static extern void ClosePseudoConsole(IntPtr hPC);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList,
            uint dwFlags,
            IntPtr Attribute,
            IntPtr lpValue,
            IntPtr cbSize,
            IntPtr lpPreviousValue,
            IntPtr lpReturnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CreateProcess(
            string? lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            bool bInheritHandles,
            int dwCreationFlags,
            IntPtr lpEnvironment,
            string? lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        // ---- instance state --------------------------------------------------

        private IntPtr _hPC = IntPtr.Zero;
        private SafeFileHandle? _ptyInputWriteSide;   // we write here -> shell stdin
        private SafeFileHandle? _ptyOutputReadSide;   // we read here  <- shell stdout
        private PROCESS_INFORMATION _processInfo;
        private IntPtr _attributeList = IntPtr.Zero;
        private FileStream? _writeStream;
        private FileStream? _readStream;
        private bool _disposed;

        public int ProcessId => _processInfo.dwProcessId;

        /// <summary>Raised whenever a chunk of raw output bytes arrives from the shell.</summary>
        public event Action<byte[], int>? OutputReceived;

        /// <summary>Raised when the shell process exits.</summary>
        public event Action<int>? ProcessExited;

        private CancellationTokenSource? _readLoopCts;

        /// <summary>
        /// Starts a new pseudo console and launches <paramref name="shellPath"/> attached to it.
        /// </summary>
        public void Start(string shellPath, string? arguments, short columns = 120, short rows = 30, string? workingDirectory = null)
        {
            if (!SafeFileHandleValid(shellPath))
                throw new FileNotFoundException($"Shell executable not found: {shellPath}");

            // Pipe pair: ConPTY writes shell output into hPipeOut-write, we read from hPipeOut-read.
            if (!CreatePipe(out SafeFileHandle inputReadSide, out _ptyInputWriteSide, IntPtr.Zero, 0))
                ThrowLastWin32("CreatePipe (input)");

            if (!CreatePipe(out _ptyOutputReadSide, out SafeFileHandle outputWriteSide, IntPtr.Zero, 0))
                ThrowLastWin32("CreatePipe (output)");

            var size = new COORD(columns, rows);
            int hr = CreatePseudoConsole(size, inputReadSide, outputWriteSide, 0, out _hPC);
            if (hr != 0)
                throw new InvalidOperationException($"CreatePseudoConsole failed, hr=0x{hr:X8}");

            // The pipe ends handed to ConPTY are duplicated internally; we can close our copies.
            inputReadSide.Close();
            outputWriteSide.Close();

            LaunchAttachedProcess(shellPath, arguments, workingDirectory);

            _writeStream = new FileStream(_ptyInputWriteSide!, FileAccess.Write);
            _readStream = new FileStream(_ptyOutputReadSide!, FileAccess.Read);

            _readLoopCts = new CancellationTokenSource();
            _ = Task.Run(() => ReadLoopAsync(_readLoopCts.Token));
            _ = Task.Run(WaitForExitAsync);
        }

        private static bool SafeFileHandleValid(string path) => File.Exists(path);

        private void LaunchAttachedProcess(string shellPath, string? arguments, string? workingDirectory)
        {
            string commandLine = string.IsNullOrWhiteSpace(arguments)
                ? $"\"{shellPath}\""
                : $"\"{shellPath}\" {arguments}";

            var startupInfo = new STARTUPINFOEX();
            startupInfo.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();

            IntPtr attrListSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrListSize);
            _attributeList = Marshal.AllocHGlobal(attrListSize);

            if (!InitializeProcThreadAttributeList(_attributeList, 1, 0, ref attrListSize))
                ThrowLastWin32("InitializeProcThreadAttributeList");

            if (!UpdateProcThreadAttribute(
                    _attributeList,
                    0,
                    PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    _hPC,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                ThrowLastWin32("UpdateProcThreadAttribute");
            }

            startupInfo.lpAttributeList = _attributeList;

            bool ok = CreateProcess(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out _processInfo);

            if (!ok)
                ThrowLastWin32("CreateProcess");
        }

        /// <summary>
        /// When true, every raw chunk read from the shell is appended to
        /// %TEMP%\RtlTerminal-debug.log as both hex and an escaped-text view (control/escape
        /// characters shown as \xNN so CSI/OSC sequences are readable). Off by default - turn on
        /// by setting the environment variable RTLTERMINAL_DEBUG=1 before launching the app, to
        /// capture the exact bytes involved in a repro instead of guessing at what a CLI sent.
        /// </summary>
        public static bool DebugLoggingEnabled { get; set; } =
            Environment.GetEnvironmentVariable("RTLTERMINAL_DEBUG") == "1";

        private static readonly string DebugLogPath =
            Path.Combine(Path.GetTempPath(), "RtlTerminal-debug.log");

        private static readonly object DebugLogLock = new();

        private static void LogDebugChunk(byte[] buffer, int count)
        {
            if (!DebugLoggingEnabled) return;
            try
            {
                string hex = BitConverter.ToString(buffer, 0, count);
                string escaped = System.Text.Encoding.UTF8.GetString(buffer, 0, count);
                var sb = new System.Text.StringBuilder();
                foreach (char c in escaped)
                {
                    if (c < 0x20 || c == 0x7F) sb.Append($"\\x{(int)c:X2}");
                    else sb.Append(c);
                }

                lock (DebugLogLock)
                {
                    File.AppendAllText(DebugLogPath,
                        $"[{DateTime.Now:HH:mm:ss.fff}] ({count} bytes)\n  HEX: {hex}\n  TXT: {sb}\n\n");
                }
            }
            catch { /* logging must never crash the terminal */ }
        }

        private async Task ReadLoopAsync(CancellationToken token)
        {
            if (_readStream is null) return;
            var buffer = new byte[4096];
            try
            {
                while (!token.IsCancellationRequested)
                {
                    int read = await _readStream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                    if (read <= 0)
                        break; // pipe closed -> process likely exited

                    LogDebugChunk(buffer, read);
                    OutputReceived?.Invoke(buffer, read);
                }
            }
            catch (OperationCanceledException) { /* expected on dispose */ }
            catch (IOException) { /* pipe closed */ }
        }

        private async Task WaitForExitAsync()
        {
            if (_processInfo.hProcess == IntPtr.Zero) return;
            using var waitHandle = new ProcessWaitHandle(_processInfo.hProcess);
            await Task.Run(() => waitHandle.WaitOne());

            NativeMethodsExitCode(_processInfo.hProcess, out int exitCode);
            ProcessExited?.Invoke(exitCode);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out int lpExitCode);

        private static void NativeMethodsExitCode(IntPtr hProcess, out int exitCode)
        {
            if (!GetExitCodeProcess(hProcess, out exitCode))
                exitCode = -1;
        }

        /// <summary>Writes raw bytes (already-encoded keystrokes / text) to the shell's stdin.</summary>
        public void Write(byte[] data)
        {
            if (_writeStream is null || _disposed) return;
            try
            {
                if (DebugLoggingEnabled) LogDebugWrite(data);
                _writeStream.Write(data, 0, data.Length);
                _writeStream.Flush();
            }
            catch (IOException) { /* shell may have exited */ }
            catch (ObjectDisposedException) { }
        }

        private static void LogDebugWrite(byte[] data)
        {
            try
            {
                string hex = BitConverter.ToString(data);
                lock (DebugLogLock)
                {
                    File.AppendAllText(DebugLogPath,
                        $"[{DateTime.Now:HH:mm:ss.fff}] SENT ({data.Length} bytes): {hex}\n\n");
                }
            }
            catch { }
        }

        public void Write(string text) => Write(System.Text.Encoding.UTF8.GetBytes(text));

        /// <summary>Resizes the pseudo console (call when the terminal control is resized).</summary>
        public void Resize(short columns, short rows)
        {
            if (_hPC == IntPtr.Zero) return;
            ResizePseudoConsole(_hPC, new COORD(columns, rows));
        }

        public void Kill()
        {
            try
            {
                if (_processInfo.hProcess != IntPtr.Zero)
                {
                    TerminateProcess(_processInfo.hProcess, 0);
                }
            }
            catch { /* best effort */ }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        private static void ThrowLastWin32(string apiName)
        {
            int err = Marshal.GetLastWin32Error();
            throw new Win32Exception(err, $"{apiName} failed (Win32 error {err})");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _readLoopCts?.Cancel(); } catch { }

            try { Kill(); } catch { }

            try { _writeStream?.Dispose(); } catch { }
            try { _readStream?.Dispose(); } catch { }

            if (_hPC != IntPtr.Zero)
            {
                ClosePseudoConsole(_hPC);
                _hPC = IntPtr.Zero;
            }

            if (_attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(_attributeList);
                Marshal.FreeHGlobal(_attributeList);
                _attributeList = IntPtr.Zero;
            }

            if (_processInfo.hThread != IntPtr.Zero) CloseHandle(_processInfo.hThread);
            if (_processInfo.hProcess != IntPtr.Zero) CloseHandle(_processInfo.hProcess);

            GC.SuppressFinalize(this);
        }
    }

    /// <summary>Minimal WaitHandle wrapper around a raw process HANDLE.</summary>
    internal sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(IntPtr processHandle)
        {
            SafeWaitHandle = new SafeWaitHandle(processHandle, ownsHandle: false);
        }
    }
}
