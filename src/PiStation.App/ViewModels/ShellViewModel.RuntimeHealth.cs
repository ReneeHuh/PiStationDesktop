using Microsoft.UI.Dispatching;
using PiStation.ClientRuntime;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Platform;

namespace PiStation.App.ViewModels;

public sealed partial class ShellViewModel
{
    public RuntimeHealthViewModel RuntimeHealth { get; } = new();
    private DispatcherQueueTimer? _activityTimer;
    private Func<bool>? _windowVisibility;
    private bool _reportingActivity;
    private bool _refreshingHealth;
    private bool _diagnosticsVisible;
    private EnvironmentId? _healthEnvironment;
    private bool _backgroundRefreshAllowed = true;
    internal bool BackgroundRefreshAllowed => _backgroundRefreshAllowed;

    internal void ObserveWindowVisibility(Func<bool> visible) => _windowVisibility = visible;
    internal void SetDiagnosticsVisible(bool visible)
    {
        _diagnosticsVisible = visible;
        if (visible) _ = RefreshRuntimeHealthAsync();
        _ = ReportWindowActivityAsync();
    }
    private void StartActivityReporting()
    {
        if (_runtimeStopped) return;
        if (_activityTimer is null)
        {
            _activityTimer = _dispatcherQueue.CreateTimer();
            _activityTimer.Interval = TimeSpan.FromSeconds(10);
            _activityTimer.Tick += OnActivityTimer;
        }
        _activityTimer.Start();
        _ = ReportWindowActivityAsync();
    }
    private async void OnActivityTimer(DispatcherQueueTimer sender, object args) => await ReportWindowActivityAsync();
    private async Task ReportWindowActivityAsync()
    {
        if (_reportingActivity || _runtimeStopped || !IsConnected || _client is not { } client) return;
        _reportingActivity = true;
        try
        {
            var environment = client.Descriptor?.EnvironmentId;
            if (_healthEnvironment != environment) { _healthEnvironment = environment; RuntimeHealth.Clear(); }
            var power = WindowsPowerState.Read();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var report = new ClientActivityReport((_windowVisibility?.Invoke() ?? _windowActive) && power.Locked != true,
                _windowActive && power.Locked != true, _diagnosticsVisible, power.OnBattery, power.LowPower);
            var result = await client.ReportClientActivityAsync(report, timeout.Token);
            if (!ReferenceEquals(_client, client) || client.Descriptor?.EnvironmentId != environment) return;
            var allowed = result.RunBackgroundRefresh && BackgroundActivityRules.IsClientEligible(result.Settings, report);
            var resumed = !_backgroundRefreshAllowed && allowed;
            _backgroundRefreshAllowed = allowed;
            RuntimeHealth.ApplyPolicy(result);
            if (_limitsVisible && allowed) await RefreshLimitsAsync();
            if (_diagnosticsVisible && allowed && result.RunDiagnostics) await RefreshRuntimeHealthAsync();
            if (resumed)
            {
                _ = RefreshRemoteWorkspaceAsync();
                _ = RefreshProjectGroupsAsync();
                _ = QueueThreadListRefreshAsync(false, CancellationToken.None);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { RuntimeHealth.Status = "Background activity report is unavailable: " + error.Message; }
        finally { _reportingActivity = false; }
    }
    public async Task RefreshRuntimeHealthAsync()
    {
        if (_refreshingHealth || !IsConnected || _client is not { } client) return;
        _refreshingHealth = true;
        var environment = client.Descriptor?.EnvironmentId;
        if (_healthEnvironment != environment) { _healthEnvironment = environment; RuntimeHealth.Clear(); }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var snapshot = await client.GetRuntimeHealthAsync(timeout.Token);
            if (ReferenceEquals(_client, client) && client.Descriptor?.EnvironmentId == environment) RuntimeHealth.Apply(snapshot);
        }
        catch (Exception error) { if (IsCurrentHealthClient(client, environment)) RuntimeHealth.Status = error.Message; }
        finally { _refreshingHealth = false; }
    }
    public async Task SaveRuntimeHealthAsync()
    {
        if (!CanOperate || _client is not { } client) return;
        var environment = client.Descriptor?.EnvironmentId;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            if (_healthEnvironment != environment) { await RefreshRuntimeHealthAsync(); return; }
            var saved = await client.SaveRuntimeHealthSettingsAsync(RuntimeHealth.CreateSettings(), timeout.Token);
            if (!IsCurrentHealthClient(client, environment)) return;
            RuntimeHealth.ApplySettings(saved);
            await ReportWindowActivityAsync();
            if (IsCurrentHealthClient(client, environment)) RuntimeHealth.Status = "Background and diagnostics settings saved on this host.";
        }
        catch (Exception error) { if (IsCurrentHealthClient(client, environment)) RuntimeHealth.Status = error.Message; }
    }
    public async Task ClearRuntimeHealthAsync()
    {
        if (!CanOperate || _client is not { } client) return;
        var environment = client.Descriptor?.EnvironmentId;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.ClearRuntimeHealthAsync(timeout.Token);
            if (IsCurrentHealthClient(client, environment)) await RefreshRuntimeHealthAsync();
        }
        catch (Exception error) { if (IsCurrentHealthClient(client, environment)) RuntimeHealth.Status = error.Message; }
    }
    internal Func<Task> PrepareDiagnosticProcessTermination(ProcessResourceSample process)
    {
        var client = RequireClient();
        var environment = client.Descriptor?.EnvironmentId;
        return async () =>
        {
            if (!CanOperate || !process.CanTerminate || _healthEnvironment != environment || !IsCurrentHealthClient(client, environment)) return;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var result = await client.TerminateDiagnosticProcessAsync(new(process.ProcessId, process.StartedUtcTicks), timeout.Token);
                if (!IsCurrentHealthClient(client, environment)) return;
                await RefreshRuntimeHealthAsync();
                if (IsCurrentHealthClient(client, environment)) RuntimeHealth.Status = result.Message;
            }
            catch (Exception error) { if (IsCurrentHealthClient(client, environment)) RuntimeHealth.Status = error.Message; }
        };
    }
    private bool IsCurrentHealthClient(IEnvironmentClient client, EnvironmentId? environment) =>
        ReferenceEquals(_client, client) && client.Descriptor?.EnvironmentId == environment;
}
