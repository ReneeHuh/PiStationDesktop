using System.Diagnostics;
using System.Runtime.InteropServices;
using PiStation.Protocol.Models;

namespace PiStation.Protocol.Platform;

public static class WindowsPowerState
{
    public static PowerState Read()
    {
        bool? locked = null, battery = null, lowPower = null;
        if (OperatingSystem.IsWindows())
        {
            if (GetSystemPowerStatus(out var power))
            {
                battery = power.AcLineStatus switch { 0 => true, 1 => false, _ => null };
                lowPower = power.SystemStatusFlag switch { 0 => false, 1 => true, _ => null };
            }
            using var process = Process.GetCurrentProcess();
            if (process.SessionId > 0 && WTSQuerySessionInformationW(IntPtr.Zero, process.SessionId, 25, out var data, out var bytes))
            {
                try
                {
                    // WTSINFOEXW's level-one union is 8-byte aligned. Read only
                    // its public header; no user/domain names are collected.
                    if (bytes >= 20 && Marshal.ReadInt32(data) == 1)
                        locked = Marshal.ReadInt32(data, 16) switch { 0 => true, 1 => false, _ => null };
                }
                finally { WTSFreeMemory(data); }
            }
        }
        return new(locked, battery, lowPower, DateTimeOffset.UtcNow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
    [DllImport("wtsapi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int session, int infoClass, out IntPtr data, out int bytes);
    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);
}
