using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using PiStation.Protocol.Models;

namespace PiStation.Host.Terminals;

internal sealed class ConPtyTerminalProcess : IAsyncDisposable
{
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const int StartUseStandardHandles = 0x00000100;
    private static readonly IntPtr PseudoConsoleAttribute = (IntPtr)0x00020016;
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private readonly FileStream _input;
    private readonly SafeFileHandle _inputReadHandle;
    private readonly FileStream _output;
    private readonly SafeFileHandle _outputWriteHandle;
    private readonly Process _process;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private IntPtr _pseudoConsole;
    private bool _disposed;

    private ConPtyTerminalProcess(
        Process process,
        IntPtr pseudoConsole,
        SafeFileHandle inputReadHandle,
        SafeFileHandle inputWriteHandle,
        SafeFileHandle outputReadHandle,
        SafeFileHandle outputWriteHandle)
    {
        _process = process;
        _pseudoConsole = pseudoConsole;
        _inputReadHandle = inputReadHandle;
        _input = new FileStream(inputWriteHandle, FileAccess.Write, bufferSize: 4096, isAsync: false);
        _output = new FileStream(outputReadHandle, FileAccess.Read, bufferSize: 4096, isAsync: false);
        _outputWriteHandle = outputWriteHandle;
    }

    public Stream Output => _output;

    public bool HasExited
    {
        get
        {
            try
            {
                return _process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public static ConPtyTerminalProcess Start(
        string workingDirectory,
        TerminalShellKind shellKind,
        int columns,
        int rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            throw new PlatformNotSupportedException("Windows pseudoconsole support is unavailable.");
        }

        SafeFileHandle? inputRead = null;
        SafeFileHandle? inputWrite = null;
        SafeFileHandle? outputRead = null;
        SafeFileHandle? outputWrite = null;
        IntPtr pseudoConsole = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        ProcessInformation processInformation = default;
        try
        {
            CreatePipePair(out inputRead, out inputWrite);
            CreatePipePair(out outputRead, out outputWrite);

            ThrowForHResult(NativeMethods.CreatePseudoConsole(
                new Coord(columns, rows),
                inputRead,
                outputWrite,
                0,
                out pseudoConsole));

            var attributeListSize = IntPtr.Zero;
            _ = NativeMethods.InitializeProcThreadAttributeList(
                IntPtr.Zero,
                1,
                0,
                ref attributeListSize);
            if (attributeListSize == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not size the process attribute list.");
            }

            attributeList = Marshal.AllocHGlobal(attributeListSize);
            if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not initialize the process attribute list.");
            }

            if (!NativeMethods.UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    PseudoConsoleAttribute,
                    pseudoConsole,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not attach the pseudoconsole attribute.");
            }

            var (applicationPath, arguments) = ResolveShell(shellKind);
            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartUseStandardHandles,
                },
                AttributeList = attributeList,
            };
            var commandLine = BuildCommandLine(applicationPath, arguments);
            var processAttributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>() };
            var threadAttributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>() };
            if (!NativeMethods.CreateProcess(
                    null,
                    commandLine,
                    ref processAttributes,
                    ref threadAttributes,
                    false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                    IntPtr.Zero,
                    workingDirectory,
                    ref startupInfo,
                    out processInformation))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not start the terminal process.");
            }

            var process = Process.GetProcessById(checked((int)processInformation.ProcessId));
            var result = new ConPtyTerminalProcess(
                process,
                pseudoConsole,
                inputRead,
                inputWrite,
                outputRead,
                outputWrite);
            pseudoConsole = IntPtr.Zero;
            inputRead = null;
            inputWrite = null;
            outputRead = null;
            outputWrite = null;
            return result;
        }
        finally
        {
            if (processInformation.Thread != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(processInformation.Thread);
            }

            if (processInformation.Process != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(processInformation.Process);
            }

            if (attributeList != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            if (pseudoConsole != IntPtr.Zero)
            {
                NativeMethods.ClosePseudoConsole(pseudoConsole);
            }

            inputRead?.Dispose();
            inputWrite?.Dispose();
            outputRead?.Dispose();
            outputWrite?.Dispose();
        }
    }

    public async Task WriteAsync(string data, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var bytes = Utf8NoBom.GetBytes(data);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _input.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Resize(int columns, int rows)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowForHResult(NativeMethods.ResizePseudoConsole(_pseudoConsole, new Coord(columns, rows)));
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var exitCode = _process.ExitCode;
        ClosePseudoConsole();
        return exitCode;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!HasExited)
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }

        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        ClosePseudoConsole();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _disposed = true;
            ClosePseudoConsole();
            _input.Dispose();
            _output.Dispose();
            _process.Dispose();
            _writeGate.Dispose();
        }
    }

    private static void CreatePipePair(out SafeFileHandle readHandle, out SafeFileHandle writeHandle)
    {
        if (!NativeMethods.CreatePipe(out readHandle, out writeHandle, IntPtr.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create a pseudoconsole pipe.");
        }
    }

    private static void ThrowForHResult(int result)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    private void ClosePseudoConsole()
    {
        var handle = Interlocked.Exchange(ref _pseudoConsole, IntPtr.Zero);
        if (handle != IntPtr.Zero)
        {
            NativeMethods.ClosePseudoConsole(handle);
            _inputReadHandle.Dispose();
            _outputWriteHandle.Dispose();
        }
    }

    private static string BuildCommandLine(string applicationPath, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder(QuoteArgument(applicationPath));
        foreach (var argument in arguments)
        {
            builder.Append(' ').Append(QuoteArgument(argument));
        }

        return builder.ToString();
    }

    private static string QuoteArgument(string argument) =>
        argument.Any(char.IsWhiteSpace) || argument.Contains('"', StringComparison.Ordinal)
            ? $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : argument;

    private static (string ApplicationPath, string[] Arguments) ResolveShell(TerminalShellKind shellKind) => shellKind switch
    {
        TerminalShellKind.PowerShell => (FindPowerShell(), ["-NoLogo"]),
        TerminalShellKind.CommandPrompt => (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", ["/D"]),
        _ => throw new ArgumentOutOfRangeException(nameof(shellKind)),
    };

    private static string FindPowerShell()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
        {
            foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim(), "pwsh.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                    // Ignore malformed PATH entries and continue to Windows PowerShell.
                }
            }
        }

        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var fallback = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(fallback) ? fallback : "powershell.exe";
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(int columns, int rows)
    {
        public readonly short X = checked((short)columns);
        public readonly short Y = checked((short)rows);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Count;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreatePipe(
            out SafeFileHandle readPipe,
            out SafeFileHandle writePipe,
            IntPtr pipeAttributes,
            uint size);

        [DllImport("kernel32.dll")]
        public static extern int CreatePseudoConsole(
            Coord size,
            SafeFileHandle input,
            SafeFileHandle output,
            uint flags,
            out IntPtr pseudoConsole);

        [DllImport("kernel32.dll")]
        public static extern int ResizePseudoConsole(IntPtr pseudoConsole, Coord size);

        [DllImport("kernel32.dll")]
        public static extern void ClosePseudoConsole(IntPtr pseudoConsole);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool InitializeProcThreadAttributeList(
            IntPtr attributeList,
            int attributeCount,
            int flags,
            ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateProcThreadAttribute(
            IntPtr attributeList,
            uint flags,
            IntPtr attribute,
            IntPtr value,
            IntPtr size,
            IntPtr previousValue,
            IntPtr returnSize);

        [DllImport("kernel32.dll")]
        public static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateProcess(
            string? applicationName,
            string commandLine,
            ref SecurityAttributes processAttributes,
            ref SecurityAttributes threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
