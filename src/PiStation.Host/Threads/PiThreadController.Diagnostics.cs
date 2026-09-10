namespace PiStation.Host.Threads;

public sealed partial class PiThreadController
{
    internal Diagnostics.OwnedProcessRoot? DiagnosticRoot
    {
        get
        {
            var process = _process;
            if (process is null || process.Exit.IsCompleted) return null;
            try { return new(process.Id, process.StartedUtcTicks, "pi", _thread.ThreadId.Value); }
            catch (InvalidOperationException) { return null; }
        }
    }
}
