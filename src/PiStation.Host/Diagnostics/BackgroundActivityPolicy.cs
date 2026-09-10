using PiStation.Protocol.Models;

namespace PiStation.Host.Diagnostics;

public sealed class BackgroundActivityPolicy(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, (ClientActivityReport Report, DateTimeOffset Expires)> _clients = [];
    public void Report(string connection, ClientActivityReport report)
    {
        lock (_gate)
        {
            Prune();
            if (!_clients.ContainsKey(connection) && _clients.Count >= 256) throw new InvalidOperationException("Too many activity clients.");
            _clients[connection] = (report, _clock.GetUtcNow().AddSeconds(45));
        }
    }
    public void Remove(string connection) { lock (_gate) _clients.Remove(connection); }

    public BackgroundPolicySnapshot Evaluate(RuntimeHealthSettings settings, PowerState power)
    {
        lock (_gate)
        {
            Prune();
            var age = _clock.GetUtcNow() - power.CapturedUtc;
            if (age > TimeSpan.FromSeconds(60) || age < TimeSpan.Zero) power = power with { Locked = null, OnBattery = null, LowPower = null };
            var reason = settings.PauseWhenHostLocked && power.Locked == true ? "Host is locked" :
                settings.PauseWhenHostLowPower && power.LowPower == true ? "Host battery saver is active" :
                settings.PauseWhenOnBattery && power.OnBattery == true ? "Host is on battery" : null;
            var clients = _clients.Values.Where(client => BackgroundActivityRules.IsClientEligible(settings, client.Report)).ToArray();
            var run = reason is null && clients.Length > 0;
            return new(settings, power, _clients.Count, run, run && clients.Any(client => client.Report.DiagnosticsVisible),
                reason ?? (run ? "Foreground refresh is active" : "Waiting for an eligible foreground client"), _clock.GetUtcNow());
        }
    }
    private void Prune()
    {
        var now = _clock.GetUtcNow();
        foreach (var id in _clients.Where(client => client.Value.Expires <= now).Select(client => client.Key).ToArray()) _clients.Remove(id);
    }
}
