using PiStation.Host.Persistence;
using PiStation.PiRpc.Process;
using PiStation.Protocol.Models;

namespace PiStation.Host.Threads;

public interface IPiProcessFactory
{
    Task<PiProcess> StartAsync(
        ProjectDescriptor project,
        HostThreadRecord thread,
        CancellationToken cancellationToken = default);
}

public sealed class PiProcessFactory(HostOptions options) : IPiProcessFactory
{
    private readonly HostOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    public Task<PiProcess> StartAsync(
        ProjectDescriptor project,
        HostThreadRecord thread,
        CancellationToken cancellationToken = default)
    {
        var installation = _options.PiInstallation ??
            throw new InvalidOperationException("No compatible Pi installation is configured.");
        var additionalArguments = _options.AdditionalPiArguments.ToList();
        var environmentVariables = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var extensions = _options.Extensions;
        if (_options.ManagementExtensionPath is { } managementPath)
        {
            additionalArguments.Add("--extension");
            additionalArguments.Add(managementPath);
        }
        foreach (var extension in extensions.Paths ?? [])
        {
            additionalArguments.Add("--extension");
            additionalArguments.Add(extension);
        }
        if (_options.BrowserAutomationExtensionPath is { } extensionPath &&
            _options.BrowserAutomationRoot is { } automationRoot)
        {
            additionalArguments.Add("--extension");
            additionalArguments.Add(Path.GetFullPath(extensionPath));
            environmentVariables["PISTATION_BROWSER_AUTOMATION_ROOT"] = Path.GetFullPath(automationRoot);
            environmentVariables["PISTATION_BROWSER_THREAD_ID"] = thread.ThreadId.Value;
        }

        return PiProcessLauncher.StartAsync(
            new PiProcessLaunchOptions
            {
                Installation = installation,
                ProjectDirectory = thread.WorkspaceMode == ThreadWorkspaceMode.Worktree
                    ? thread.WorktreePath ?? throw new InvalidOperationException("The thread worktree path is missing.")
                    : project.CanonicalPath,
                SessionDirectory = _options.SessionRoot,
                SessionId = thread.PiSessionId,
                AdditionalArguments = additionalArguments,
                DiscoverExtensions = extensions.DiscoverInstalled,
                EnvironmentVariables = environmentVariables,
            },
            cancellationToken);
    }
}
