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
        return PiProcessLauncher.StartAsync(
            new PiProcessLaunchOptions
            {
                Installation = installation,
                ProjectDirectory = thread.WorkspaceMode == ThreadWorkspaceMode.Worktree
                    ? thread.WorktreePath ?? throw new InvalidOperationException("The thread worktree path is missing.")
                    : project.CanonicalPath,
                SessionDirectory = _options.SessionRoot,
                SessionId = thread.PiSessionId,
                AdditionalArguments = _options.AdditionalPiArguments,
            },
            cancellationToken);
    }
}
