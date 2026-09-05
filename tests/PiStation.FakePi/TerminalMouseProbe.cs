using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PiStation.FakePi;

internal static class TerminalMouseProbe
{
    private const uint EnableEchoInput = 0x0004;
    private const uint EnableExtendedFlags = 0x0080;
    private const uint EnableLineInput = 0x0002;
    private const uint EnableMouseInput = 0x0010;
    private const uint EnableQuickEditMode = 0x0040;
    private const uint EnableVirtualTerminalInput = 0x0200;
    private const uint FromLeftFirstButtonPressed = 0x0001;
    private const ushort MouseEvent = 0x0002;
    private const int StandardInputHandle = -10;

    public static int Run(bool transitionOnly)
    {
        var input = NativeMethods.GetStdHandle(StandardInputHandle);
        if (input == IntPtr.Zero || input == new IntPtr(-1) ||
            !NativeMethods.GetConsoleMode(input, out var originalMode))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the console input mode.");
        }

        var mouseMode = originalMode;
        mouseMode &= ~(EnableEchoInput | EnableLineInput | EnableQuickEditMode | EnableVirtualTerminalInput);
        mouseMode |= EnableExtendedFlags | EnableMouseInput;
        if (!NativeMethods.SetConsoleMode(input, mouseMode))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not enable console mouse input.");
        }

        try
        {
            Console.WriteLine(string.Concat("MOUSE-", "READY"));
            if (transitionOnly)
            {
                Thread.Sleep(TimeSpan.FromSeconds(3));
                return 0;
            }

            MouseEventRecord? pressed = null;
            while (true)
            {
                if (!NativeMethods.ReadConsoleInput(input, out var inputRecord, 1, out var recordsRead))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read console mouse input.");
                }

                if (recordsRead == 0 || inputRecord.EventType != MouseEvent)
                {
                    continue;
                }

                var mouse = inputRecord.MouseEvent;
                if ((mouse.ButtonState & FromLeftFirstButtonPressed) != 0)
                {
                    pressed = mouse;
                }
                else if (pressed is { } press)
                {
                    Console.WriteLine(string.Create(
                        System.Globalization.CultureInfo.InvariantCulture,
                        $"MOUSE-RECEIVED:LEFT:{press.MousePosition.X}:{press.MousePosition.Y}"));
                    return 0;
                }
            }
        }
        finally
        {
            _ = NativeMethods.SetConsoleMode(input, originalMode);
            if (transitionOnly)
            {
                Console.WriteLine(string.Concat("MOUSE-MODE-", "RESTORED"));
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord
    {
        public readonly short X;
        public readonly short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MouseEventRecord
    {
        public readonly Coord MousePosition;
        public readonly uint ButtonState;
        public readonly uint ControlKeyState;
        public readonly uint EventFlags;
    }

    [StructLayout(LayoutKind.Explicit)]
    private readonly struct InputRecord
    {
        [FieldOffset(0)]
        public readonly ushort EventType;

        [FieldOffset(4)]
        public readonly MouseEventRecord MouseEvent;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetStdHandle(int standardHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetConsoleMode(IntPtr consoleHandle, out uint mode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetConsoleMode(IntPtr consoleHandle, uint mode);

        [DllImport("kernel32.dll", EntryPoint = "ReadConsoleInputW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReadConsoleInput(
            IntPtr consoleInput,
            out InputRecord buffer,
            uint length,
            out uint eventsRead);
    }
}
