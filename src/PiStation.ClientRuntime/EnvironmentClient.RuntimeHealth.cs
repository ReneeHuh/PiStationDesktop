using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime;

public sealed partial class EnvironmentClient
{
    public Task<RuntimeHealthSnapshot> GetRuntimeHealthAsync(CancellationToken token = default) => InvokeAsync<RuntimeHealthSnapshot>("GetRuntimeHealth", token);
    public Task<RuntimeHealthSettings> SaveRuntimeHealthSettingsAsync(RuntimeHealthSettings settings, CancellationToken token = default) => InvokeAsync<RuntimeHealthSettings>("SaveRuntimeHealthSettings", settings, token);
    public Task ClearRuntimeHealthAsync(CancellationToken token = default) => InvokeAsync<bool>("ClearRuntimeHealth", token);
    public Task<BackgroundPolicySnapshot> ReportClientActivityAsync(ClientActivityReport report, CancellationToken token = default) => InvokeAsync<BackgroundPolicySnapshot>("ReportClientActivity", report, token);
    public Task<DiagnosticActionResult> TerminateDiagnosticProcessAsync(TerminateDiagnosticProcessRequest request, CancellationToken token = default) => InvokeAsync<DiagnosticActionResult>("TerminateDiagnosticProcess", request, token);
}
