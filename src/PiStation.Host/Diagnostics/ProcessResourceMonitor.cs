using System.Diagnostics;
using PiStation.Host.Terminals;
using PiStation.Protocol.Models;

namespace PiStation.Host.Diagnostics;

internal sealed record OwnedProcessRoot(int Id, long StartedUtcTicks, string Kind, string OwnerId);

internal sealed class ProcessResourceMonitor(Func<IEnumerable<OwnedProcessRoot>> roots)
{
    private readonly Dictionary<(int, long), (double Cpu, long Timestamp)> _previous = [];
    private readonly Func<IEnumerable<OwnedProcessRoot>> _roots = roots;

    public IReadOnlyList<ProcessResourceSample> Capture()
    {
        var entries = OperatingSystem.IsWindows() ? TerminalProcessInspector.Capture() : [];
        var children = entries.ToLookup(entry => entry.ParentId);
        var result = new List<ProcessResourceSample>();
        var seen = new HashSet<int>();
        var now = Stopwatch.GetTimestamp();
        using var host = Process.GetCurrentProcess();
        Add(host, null, "host", "", 0, false);
        foreach (var root in _roots().ToArray())
        {
            try
            {
                using var owner = Process.GetProcessById(root.Id);
                if (owner.StartTime.ToUniversalTime().Ticks != root.StartedUtcTicks || owner.HasExited) continue;
                Add(owner, host.Id, root.Kind, root.OwnerId, 1, true);
                Visit(root.Id, root.StartedUtcTicks, root.Kind, root.OwnerId, 2);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        var identities = result.Select(item => (item.ProcessId, item.StartedUtcTicks)).ToHashSet();
        foreach (var key in _previous.Keys.Where(key => !identities.Contains(key)).ToArray()) _previous.Remove(key);
        return result;

        void Visit(int parent, long parentStarted, string kind, string ownerId, int depth)
        {
            if (depth > 32 || result.Count >= 256) return;
            foreach (var entry in children[parent])
            {
                if (seen.Contains(entry.Id) || result.Count >= 256) continue;
                try
                {
                    using var child = Process.GetProcessById(entry.Id);
                    var started = child.StartTime.ToUniversalTime().Ticks;
                    if (started < parentStarted || child.HasExited) continue;
                    Add(child, parent, kind + " child", ownerId, depth, true);
                    Visit(entry.Id, started, kind, ownerId, depth + 1);
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        void Add(Process process, int? parent, string kind, string owner, int depth, bool canTerminate)
        {
            if (!seen.Add(process.Id) || result.Count >= 256) return;
            var started = process.StartTime.ToUniversalTime().Ticks;
            var cpu = process.TotalProcessorTime.TotalSeconds;
            double? percent = null;
            if (_previous.TryGetValue((process.Id, started), out var previous) && Stopwatch.GetElapsedTime(previous.Timestamp, now).TotalSeconds is > 0 and var elapsed)
                percent = Math.Clamp((cpu - previous.Cpu) / elapsed / Environment.ProcessorCount * 100, 0, 100);
            _previous[(process.Id, started)] = (cpu, now);
            result.Add(new(process.Id, started, parent, process.ProcessName, kind, owner, depth, percent,
                process.WorkingSet64, process.PrivateMemorySize64, canTerminate));
        }
    }

    public DiagnosticActionResult Terminate(TerminateDiagnosticProcessRequest request)
    {
        if (request.ProcessId == Environment.ProcessId) return new(false, "The host process cannot be terminated here.");
        var selected = Capture().FirstOrDefault(process => process.ProcessId == request.ProcessId && process.StartedUtcTicks == request.StartedUtcTicks && process.CanTerminate);
        if (selected is null) return new(false, "The selected process exited, changed identity, or is no longer owned by this environment.");
        try
        {
            using var process = Process.GetProcessById(selected.ProcessId);
            _ = process.Handle; // Pin this kernel process object before the identity check and action.
            if (process.StartTime.ToUniversalTime().Ticks != selected.StartedUtcTicks || process.HasExited)
                return new(false, "The selected process identity changed.");
            process.Kill(entireProcessTree: false);
            return new(true, "Termination requested for the selected process.");
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return new(false, "Process termination failed: " + error.Message); }
    }
}
