using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PiStation.Protocol.Models;

namespace PiStation.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private bool _manageAutoCompaction;
    private bool _autoCompaction = true;
    private bool _manageAutoRetry;
    private bool _autoRetry = true;
    private string _automationStatus = "Preferences have not loaded.";
    internal long AutomationRevision { get; private set; } = -1;
    public bool ManageAutoCompaction { get => _manageAutoCompaction; set => SetProperty(ref _manageAutoCompaction, value); }
    public bool AutoCompaction { get => _autoCompaction; set => SetProperty(ref _autoCompaction, value); }
    public bool ManageAutoRetry { get => _manageAutoRetry; set => SetProperty(ref _manageAutoRetry, value); }
    public bool AutoRetry { get => _autoRetry; set => SetProperty(ref _autoRetry, value); }
    public string AutomationStatus { get => _automationStatus; internal set => SetProperty(ref _automationStatus, value); }
    internal void ApplyAutomationSettings(PiAutomationSettings settings)
    {
        AutomationRevision = settings.Revision;
        ManageAutoCompaction = settings.AutoCompaction.HasValue;
        AutoCompaction = settings.AutoCompaction ?? true;
        ManageAutoRetry = settings.AutoRetry.HasValue;
        AutoRetry = settings.AutoRetry ?? true;
    }
    private bool _autoSettleInactive = true;
    private double _autoSettleDays = 3;
    private bool _autoSettleMerged = true;
    private bool _autoSettleClosed = true;
    public bool AutoSettleInactive { get => _autoSettleInactive; set => SetProperty(ref _autoSettleInactive, value); }
    public double AutoSettleDays { get => _autoSettleDays; set => SetProperty(ref _autoSettleDays, value); }
    public bool AutoSettleMerged { get => _autoSettleMerged; set => SetProperty(ref _autoSettleMerged, value); }
    public bool AutoSettleClosed { get => _autoSettleClosed; set => SetProperty(ref _autoSettleClosed, value); }
    internal void ApplySettlement(SettlementSettings settings)
    {
        AutoSettleInactive = settings.InactiveDays is not null;
        AutoSettleDays = settings.InactiveDays ?? 3;
        AutoSettleMerged = settings.OnMerge;
        AutoSettleClosed = settings.OnClose;
    }
    private bool _isBusy;
    private bool _canOpenUpdateInstaller;
    public bool CanOpenUpdateInstaller { get => _canOpenUpdateInstaller; internal set => SetProperty(ref _canOpenUpdateInstaller, value); }
    private string _piExecutablePath = string.Empty;
    private string _runtimeSetupStatus = "Checking Pi…";

    public string PiExecutablePath { get => _piExecutablePath; set => SetProperty(ref _piExecutablePath, value); }
    public string RuntimeSetupStatus { get => _runtimeSetupStatus; internal set => SetProperty(ref _runtimeSetupStatus, value); }
    private bool _discoverPiExtensions;
    private string _piArguments = string.Empty;
    private string _piEnvironment = string.Empty;
    private double _piCommandTimeout = 30;
    private double _piShutdownTimeout = 3;
    public string PiArguments { get => _piArguments; set => SetProperty(ref _piArguments, value); }
    public string PiEnvironment { get => _piEnvironment; set => SetProperty(ref _piEnvironment, value); }
    public double PiCommandTimeout { get => _piCommandTimeout; set => SetProperty(ref _piCommandTimeout, value); }
    public double PiShutdownTimeout { get => _piShutdownTimeout; set => SetProperty(ref _piShutdownTimeout, value); }
    private string _piExtensionPaths = string.Empty;
    public bool DiscoverPiExtensions { get => _discoverPiExtensions; set => SetProperty(ref _discoverPiExtensions, value); }
    public string PiExtensionPaths { get => _piExtensionPaths; set => SetProperty(ref _piExtensionPaths, value); }
    private string _diagnosticsSummary = "Diagnostics have not been loaded.";
    private string _sourceControlSummary = "Select a project to inspect its hosting provider.";
    private string _status = string.Empty;
    private string _updateSummary = "Update channel information is unavailable.";
    private string _usageSummary = "No recorded usage.";

    public ObservableCollection<RuntimeDiagnostic> Diagnostics { get; } = [];

    public ObservableCollection<string> Logs { get; } = [];

    private bool _canCreatePullRequest;
    public bool CanCreatePullRequest { get => _canCreatePullRequest; private set => SetProperty(ref _canCreatePullRequest, value); }

    public ObservableCollection<PullRequestDescriptor> PullRequests { get; } = [];

    public ObservableCollection<HostingOperation> HostingOperations { get; } = [];

    internal void ApplyHostingOperations(IReadOnlyList<HostingOperation> operations)
    {
        HostingOperations.Clear();
        foreach (var operation in operations) HostingOperations.Add(operation);
    }

    public bool IsBusy
    {
        get => _isBusy;
        internal set => SetProperty(ref _isBusy, value);
    }

    public string Status
    {
        get => _status;
        internal set => SetProperty(ref _status, value);
    }

    public string DiagnosticsSummary
    {
        get => _diagnosticsSummary;
        private set => SetProperty(ref _diagnosticsSummary, value);
    }

    public string UsageSummary
    {
        get => _usageSummary;
        private set => SetProperty(ref _usageSummary, value);
    }

    public string UpdateSummary
    {
        get => _updateSummary;
        internal set => SetProperty(ref _updateSummary, value);
    }

    public string SourceControlSummary
    {
        get => _sourceControlSummary;
        internal set => SetProperty(ref _sourceControlSummary, value);
    }

    internal void ApplyDiagnostics(DiagnosticsSnapshot snapshot)
    {
        Diagnostics.Clear();
        foreach (var diagnostic in snapshot.Diagnostics)
        {
            Diagnostics.Add(diagnostic);
        }

        Logs.Clear();
        foreach (var log in snapshot.RecentLogs)
        {
            Logs.Add(log);
        }

        var resources = snapshot.Resources;
        DiagnosticsSummary = string.Create(
            CultureInfo.InvariantCulture,
            $"Pi Station {snapshot.ApplicationVersion} • protocol {snapshot.ProtocolVersion} • " +
            $"{FormatBytes(resources.WorkingSetBytes)} working set • {resources.ProcessThreadCount} OS threads • " +
            $"{resources.ActiveThreadRuntimes} Pi runtimes • {resources.ActiveTerminalSessions} terminals • " +
            $"database {FormatBytes(resources.DatabaseBytes)}");
        UsageSummary = string.Create(
            CultureInfo.InvariantCulture,
            $"{snapshot.Usage.TotalTokens:N0} tokens • {FormatCost(snapshot.Usage.EstimatedCost)} • " +
            $"{snapshot.Usage.QuotaState}: {snapshot.Usage.QuotaDetail}");
        UpdateSummary = snapshot.UpdateState;
    }

    public int? NextPullRequestOffset { get; private set; }
    public PullRequestState? PullRequestStateFilter { get; private set; }
    public PullRequestListFilters? PullRequestFilters { get; private set; }
    public long PullRequestQueryVersion { get; private set; }
    public void SetPullRequestFilters(PullRequestState? state, PullRequestListFilters? filters)
    {
        PullRequestStateFilter = state;
        PullRequestFilters = filters;
        PullRequestQueryVersion++;
        ClearSourceControl("Loading filtered pull requests…");
    }
    public bool CanLoadMorePullRequests => NextPullRequestOffset is not null;

    internal void ApplyPullRequests(ListPullRequestsResult result, bool append = false)
    {
        CanCreatePullRequest = false;
        if (!append) PullRequests.Clear();
        NextPullRequestOffset = result.NextOffset;
        OnPropertyChanged(nameof(CanLoadMorePullRequests));
        foreach (var pullRequest in result.PullRequests)
        {
            if (!PullRequests.Any(existing => existing.Url == pullRequest.Url)) PullRequests.Add(pullRequest);
        }

        CanCreatePullRequest = result.Repository.CanWrite && HostingCapabilities.CanCreate(result.Repository.Provider);
        SourceControlSummary = $"{result.Repository.Provider} • {result.Repository.Owner}/{result.Repository.Name} • " +
            $"{PullRequests.Count} pull requests • {(result.Repository.CanWrite ? "Authenticated" : "Authentication required for writes")}" +
            (result.Notice is null ? "" : " • " + result.Notice);
    }

    internal void ClearSourceControl(string status)
    {
        NextPullRequestOffset = null;
        OnPropertyChanged(nameof(CanLoadMorePullRequests));
        CanCreatePullRequest = false;
        PullRequests.Clear();
        SourceControlSummary = status;
    }

    private static string FormatBytes(long bytes)
    {
        var value = (double)Math.Max(0, bytes);
        var unit = "B";
        foreach (var candidate in new[] { "KB", "MB", "GB", "TB" })
        {
            if (value < 1024)
            {
                break;
            }

            value /= 1024;
            unit = candidate;
        }

        return $"{value:0.#} {unit}";
    }

    private static string FormatCost(decimal? cost) => cost is { } value
        ? string.Create(CultureInfo.InvariantCulture, $"${value:0.0000} reported estimate")
        : "Cost unavailable for some or all turns";
}
