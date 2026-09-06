using PiStation.Protocol.Identifiers;

namespace PiStation.Protocol.Models;

public sealed record PiResourceDescriptor(string Id, string Kind, string Name, string Path, string Source,
    string Scope, bool Enabled, bool ConfirmedLoaded, bool CanToggle, string Revision);
public sealed record PiProviderStatus(string ProviderId, string DisplayName, bool CredentialConfigured,
    string CredentialSource, int ModelCount);
public sealed record PiResourcesSnapshot(string AgentDirectory, string ProjectDirectory, bool ProjectTrusted,
    bool? SavedProjectTrust, IReadOnlyList<PiResourceDescriptor> Resources, IReadOnlyList<PiProviderStatus> Providers,
    IReadOnlyList<string> Diagnostics, string ModelsRevision, string Message);
public sealed record PiCustomModel(string ProviderId, string ModelId, string DisplayName, string BaseUrl, string Api,
    string? ApiKeyEnvironmentVariable = null, bool Keyless = false, bool Reasoning = false);
public sealed record ManagePiResourcesRequest(ThreadId ThreadId, string Action = "inspect", string? ResourceId = null,
    bool? Enabled = null, string? Revision = null, PiCustomModel? Model = null);
public sealed record StartPiSetupRequest(ProjectId ProjectId, ThreadId? ThreadId, string Action);
public sealed record PiSetupTerminalResult(TerminalSessionDescriptor Terminal, string Instructions);
