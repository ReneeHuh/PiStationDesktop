using PiStation.Host.Persistence;
using PiStation.Host.Errors;
using PiStation.Host.Threads;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Host.Projects;

public sealed class ProjectService(HostDatabase database)
{
    private readonly HostDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<ProjectDescriptor> AddAsync(
        AddProjectRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Path);
        var canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Path));
        if (!Directory.Exists(canonicalPath))
        {
            throw new DirectoryNotFoundException($"Project directory does not exist: {canonicalPath}");
        }

        var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? new DirectoryInfo(canonicalPath).Name
            : request.DisplayName.Trim();
        var configuration = await ProjectConfigurationLoader.LoadAsync(canonicalPath, cancellationToken)
            .ConfigureAwait(false);
        var project = await _database.AddProjectAsync(
            canonicalPath,
            displayName,
            configuration.DefaultWorkspaceMode,
            configuration.Scripts,
            cancellationToken).ConfigureAwait(false);
        await _database.UpdateProjectConfigurationAsync(
            project.ProjectId,
            configuration.DefaultWorkspaceMode,
            configuration.Scripts,
            cancellationToken).ConfigureAwait(false);
        return project with
        {
            DefaultWorkspaceMode = configuration.DefaultWorkspaceMode,
            Scripts = configuration.Scripts,
        };
    }

    public async Task<IReadOnlyList<ProjectDescriptor>> ListAsync(CancellationToken cancellationToken = default)
    {
        var projects = await _database.ListProjectsAsync(cancellationToken).ConfigureAwait(false);
        var refreshed = new List<ProjectDescriptor>(projects.Count);
        foreach (var project in projects)
        {
            var configuration = await ProjectConfigurationLoader.LoadAsync(project.CanonicalPath, cancellationToken)
                .ConfigureAwait(false);
            if (configuration.DefaultWorkspaceMode != project.DefaultWorkspaceMode ||
                !configuration.Scripts.SequenceEqual(project.Scripts ?? []))
            {
                await _database.UpdateProjectConfigurationAsync(
                    project.ProjectId,
                    configuration.DefaultWorkspaceMode,
                    configuration.Scripts,
                    cancellationToken).ConfigureAwait(false);
                refreshed.Add(project with
                {
                    DefaultWorkspaceMode = configuration.DefaultWorkspaceMode,
                    Scripts = configuration.Scripts,
                });
            }
            else
            {
                refreshed.Add(project);
            }
        }

        return refreshed;
    }

    public async Task<ProjectDescriptor> SetScriptsTrustAsync(
        SetProjectScriptsTrustRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await _database.GetProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.ProjectNotFound,
                $"Project '{request.ProjectId}' was not found.");
        }

        return await _database.SetProjectScriptsTrustAsync(request.ProjectId, request.IsTrusted, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ThreadDescriptor> CreateThreadAsync(
        CreateThreadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var project = await _database.GetProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Project '{request.ProjectId}' was not found.");
        var threads = await _database.ListThreadsAsync(
            project.ProjectId,
            includeArchived: true,
            cancellationToken).ConfigureAwait(false);
        var title = ThreadMetadataValidation.NormalizeTitle(
            string.IsNullOrWhiteSpace(request.Title) ? $"Thread {threads.Count + 1}" : request.Title);
        var thread = await _database.CreateThreadAsync(
            project.ProjectId,
            title,
            ThreadWorkspaceMode.Local,
            branchName: null,
            worktreePath: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return thread.ToDescriptor(_database.EnvironmentId);
    }

    public async Task<IReadOnlyList<ThreadDescriptor>> ListThreadsAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        var threads = await _database.ListThreadsAsync(projectId, cancellationToken).ConfigureAwait(false);
        return threads.Select(thread => thread.ToDescriptor(_database.EnvironmentId)).ToArray();
    }

    public async Task<SearchThreadsResult> SearchThreadsAsync(
        SearchThreadsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Limit is < 1 or > ThreadLifecycleDefaults.MaximumSearchLimit)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.ThreadSearchInvalid,
                $"The thread search limit must be between 1 and {ThreadLifecycleDefaults.MaximumSearchLimit}.");
        }

        if (await _database.GetProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.ProjectNotFound,
                $"Project '{request.ProjectId}' was not found.");
        }

        var query = ThreadMetadataValidation.NormalizeSearchQuery(request.Query);
        var threads = await _database.SearchThreadsAsync(
            request.ProjectId,
            query,
            request.IncludeArchived,
            request.Limit + 1,
            cancellationToken).ConfigureAwait(false);
        return new SearchThreadsResult(
            threads.Take(request.Limit)
                .Select(thread => thread.ToDescriptor(_database.EnvironmentId))
                .ToArray(),
            threads.Count > request.Limit);
    }
}
