using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed class PiResourceRow(PiResourceDescriptor resource)
{
    public PiResourceDescriptor Resource { get; } = resource;
    public string Heading => $"{Resource.Kind} · {Resource.Name}";
    public string Source => $"{Resource.Scope} · {Resource.Source}";
    public string Path => Resource.Path;
    public string State => (Resource.ConfirmedLoaded ? "Confirmed in this runtime" : "Not confirmed in this runtime") +
        (Resource.Enabled ? " · enabled in Pi settings" : " · disabled in Pi settings");
    public string ToggleLabel => Resource.Enabled ? "Disable" : "Enable";
    public bool CanToggle => Resource.CanToggle;
}

public sealed class PiResourcesViewModel : ObservableObject
{
    private bool _isBusy;
    private string _status = "Select an idle thread, then refresh its Pi resources.";
    private string _trustSummary = "Project trust has not been inspected.";
    private string _directories = string.Empty;
    private string _diagnostics = string.Empty;
    private string _providerSummary = string.Empty;
    public ThreadId? ThreadId { get; private set; }
    public PiResourcesSnapshot? Snapshot { get; private set; }
    public ObservableCollection<PiResourceRow> Resources { get; } = [];
    public bool IsBusy { get => _isBusy; internal set { if (SetProperty(ref _isBusy, value)) OnPropertyChanged(nameof(CanEdit)); } }
    public bool CanEdit => !IsBusy && Snapshot is not null;
    public string Status { get => _status; internal set => SetProperty(ref _status, value); }
    public string TrustSummary { get => _trustSummary; private set => SetProperty(ref _trustSummary, value); }
    public string Directories { get => _directories; private set => SetProperty(ref _directories, value); }
    public string Diagnostics { get => _diagnostics; private set => SetProperty(ref _diagnostics, value); }
    public string ProviderSummary { get => _providerSummary; private set => SetProperty(ref _providerSummary, value); }
    public IReadOnlyList<string> ModelApis { get; } = ["openai-completions", "openai-responses", "anthropic-messages"];
    public string ModelProvider { get; set; } = string.Empty;
    public string ModelId { get; set; } = string.Empty;
    public string ModelDisplayName { get; set; } = string.Empty;
    public string ModelBaseUrl { get; set; } = string.Empty;
    public string ModelApi { get; set; } = "openai-completions";
    public string ModelKeyEnvironmentVariable { get; set; } = string.Empty;
    public bool ModelKeyless { get; set; }
    public bool ModelReasoning { get; set; }
    public PiCustomModel CreateModel() => new(ModelProvider.Trim(), ModelId.Trim(), ModelDisplayName.Trim(),
        ModelBaseUrl.Trim(), ModelApi, ModelKeyEnvironmentVariable.Trim(), ModelKeyless, ModelReasoning);

    internal void Apply(ThreadId threadId, PiResourcesSnapshot snapshot)
    {
        ThreadId = threadId;
        Snapshot = snapshot;
        Resources.Clear();
        foreach (var resource in snapshot.Resources.OrderBy(item => item.Kind).ThenBy(item => item.Name))
            Resources.Add(new(resource));
        TrustSummary = $"This runtime: project {(snapshot.ProjectTrusted ? "trusted" : "not trusted")}. " +
            $"Saved decision: {snapshot.SavedProjectTrust switch { true => "trust", false => "do not trust", null => "Pi default policy" }}.";
        Directories = $"Project: {snapshot.ProjectDirectory}\nPi configuration: {snapshot.AgentDirectory}";
        Diagnostics = snapshot.Diagnostics.Count == 0 ? "No reported configuration or startup errors." : string.Join("\n\n", snapshot.Diagnostics);
        ProviderSummary = string.Join("\n", snapshot.Providers.Select(provider =>
            $"{provider.DisplayName} ({provider.ProviderId}) · {provider.ModelCount} models · " +
            (provider.CredentialConfigured ? $"credential configured ({provider.CredentialSource})" : "credential not configured")));
        Status = snapshot.Message;
        OnPropertyChanged(nameof(CanEdit));
    }

    internal void Clear()
    {
        ThreadId = null;
        Snapshot = null;
        Resources.Clear();
        TrustSummary = "Project trust has not been inspected.";
        Directories = Diagnostics = ProviderSummary = string.Empty;
        Status = "Refresh to inspect the selected thread.";
        OnPropertyChanged(nameof(CanEdit));
    }
}
