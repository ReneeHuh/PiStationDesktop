using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace PiStation.Host.Preview;

internal sealed class WindowsPreviewListenerSource : IPreviewListenerSource
{
    public IReadOnlyCollection<int> GetListeningPorts() => GetListeners().Select(item => item.Port).Distinct().ToArray();

    public IReadOnlyCollection<PreviewListener> GetListeners()
    {
        var listeners = new List<PreviewListener>();
        foreach (var family in new[] { 2, 23 })
        {
            try { listeners.AddRange(ReadTable(family)); }
            catch (Exception error) when (error is Win32Exception or InvalidDataException)
            { Trace.TraceWarning("Preview listener table is unavailable: {0}", error.Message); }
        }
        if (listeners.Count > 0) return listeners;
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Where(endpoint => IsLocal(endpoint.Address))
                .Select(endpoint => new PreviewListener(endpoint.Port, Host: ProbeHost(endpoint.Address))).ToArray();
        }
        catch (NetworkInformationException) { return []; }
    }

    private static List<PreviewListener> ReadTable(int family)
    {
        var size = 0;
        // TCP_TABLE_OWNER_PID_LISTENER = 3. Both row layouts contain DWORD-aligned fields.
        // https://learn.microsoft.com/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable
        var status = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
        for (var attempt = 0; attempt < 3 && status == 122; attempt++)
        {
            if (size is < 4 or > 16 * 1024 * 1024) throw new InvalidDataException("Invalid TCP table size.");
            var capacity = size;
            var buffer = Marshal.AllocHGlobal(capacity);
            try
            {
                status = GetExtendedTcpTable(buffer, ref size, false, family, 3, 0);
                if (status == 122) continue;
                if (status != 0) throw new Win32Exception(status);
                var count = Marshal.ReadInt32(buffer);
                var rowSize = family == 2 ? 24 : 56;
                if (count < 0 || count > (capacity - 4) / rowSize) throw new InvalidDataException("Truncated TCP table.");
                var result = new List<PreviewListener>();
                var names = new Dictionary<int, string?>();
                for (var index = 0; index < count; index++)
                {
                    var row = IntPtr.Add(buffer, 4 + index * rowSize);
                    var bytes = new byte[family == 2 ? 4 : 16];
                    Marshal.Copy(IntPtr.Add(row, family == 2 ? 4 : 0), bytes, 0, bytes.Length);
                    var address = new IPAddress(bytes);
                    if (!IsLocal(address)) continue;
                    var networkPort = Marshal.ReadInt32(row, family == 2 ? 8 : 20);
                    var port = ((networkPort & 255) << 8) | ((networkPort >> 8) & 255);
                    var pid = Marshal.ReadInt32(row, family == 2 ? 20 : 52);
                    if (!names.TryGetValue(pid, out var name))
                    {
                        try { using var process = Process.GetProcessById(pid); name = process.ProcessName; }
                        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { }
                        names[pid] = name;
                    }
                    result.Add(new(port, pid > 0 ? pid : null, name, ProbeHost(address)));
                }
                return result;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        if (status != 0) throw new Win32Exception(status);
        return [];
    }

    private static bool IsLocal(IPAddress address) => IPAddress.IsLoopback(address) ||
        address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
    private static string ProbeHost(IPAddress address) => address.Equals(IPAddress.Any)
        ? IPAddress.Loopback.ToString()
        : address.Equals(IPAddress.IPv6Any) ? IPAddress.IPv6Loopback.ToString() : address.ToString();

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int GetExtendedTcpTable(IntPtr table, ref int size,
        [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
}
