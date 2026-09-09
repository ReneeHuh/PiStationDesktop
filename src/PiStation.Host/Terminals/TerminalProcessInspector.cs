using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PiStation.Host.Terminals;

internal sealed record TerminalProcessEntry(int Id, int ParentId, string Name);
internal sealed record TerminalActivity(bool HasChildren, string? Command);

internal static class TerminalProcessInspector
{
    // One native snapshot serves every live terminal. No polling helper processes.
    // https://learn.microsoft.com/windows/win32/toolhelp/taking-a-snapshot-and-viewing-processes
    public static IReadOnlyList<TerminalProcessEntry> Capture()
    {
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>(), Name = "" };
        if (!Process32FirstW(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entries = new List<TerminalProcessEntry>();
        do
        {
            if (entries.Count >= 65536) throw new InvalidDataException("The process snapshot was truncated.");
            entries.Add(new(checked((int)entry.Id), checked((int)entry.ParentId), entry.Name));
        } while (Process32NextW(snapshot, ref entry));
        if (Marshal.GetLastWin32Error() != 18) throw new Win32Exception(Marshal.GetLastWin32Error());
        return entries;
    }

    public static TerminalActivity? Inspect(IReadOnlyList<TerminalProcessEntry> entries, int shellId)
    {
        if (!entries.Any(entry => entry.Id == shellId)) return null;
        var child = entries.Where(entry => entry.ParentId == shellId && entry.Id != shellId).MinBy(entry => entry.Id);
        var name = child?.Name.Replace('\\', '/').Split('/')[^1].Trim();
        if (name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true) name = name[..^4];
        if (name is { Length: > 128 }) name = name[..128];
        return new(child is not null, string.IsNullOrWhiteSpace(name) ? null : name);
    }

    internal static IReadOnlyCollection<int> Descendants(IReadOnlyList<TerminalProcessEntry> entries, int shellId)
    {
        if (!entries.Any(entry => entry.Id == shellId)) return [];
        var children = entries.ToLookup(entry => entry.ParentId);
        var visited = new HashSet<int> { shellId };
        var pending = new Stack<int>();
        pending.Push(shellId);
        while (pending.TryPop(out var parent))
            foreach (var child in children[parent])
                if (child.Id > 0 && visited.Add(child.Id)) pending.Push(child.Id);
        // An idle shell has no registered server processes.
        return visited.Count > 1 ? visited : [];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Id;
        public UIntPtr DefaultHeap;
        public uint Module, Threads, ParentId;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
}
