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
            configuration.Icon,
            configuration.DefaultModel,
            configuration.DefaultThinkingLevel,
            configuration.DefaultRuntimeModeId,
            configuration.AutoPullDefaultBranch,
            cancellationToken).ConfigureAwait(false);
        configuration = await ApplyOverridesAsync(project.ProjectId, configuration, cancellationToken).ConfigureAwait(false);
        await _database.UpdateProjectConfigurationAsync(
            project.ProjectId,
            configuration.DefaultWorkspaceMode,
            configuration.Scripts,
            configuration.Icon,
            configuration.DefaultModel,
            configuration.DefaultThinkingLevel,
            configuration.DefaultRuntimeModeId,
            configuration.AutoPullDefaultBranch,
            cancellationToken).ConfigureAwait(false);
        return project with
        {
            DefaultWorkspaceMode = configuration.DefaultWorkspaceMode,
            Scripts = configuration.Scripts,
            Icon = configuration.Icon,
            DefaultModel = configuration.DefaultModel,
            DefaultThinkingLevel = configuration.DefaultThinkingLevel,
            DefaultRuntimeModeId = configuration.DefaultRuntimeModeId,
            AutoPullDefaultBranch = configuration.AutoPullDefaultBranch,
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
            configuration = await ApplyOverridesAsync(project.ProjectId, configuration, cancellationToken).ConfigureAwait(false);
            if (configuration.DefaultWorkspaceMode != project.DefaultWorkspaceMode ||
                !configuration.Scripts.SequenceEqual(project.Scripts ?? []) ||
                configuration.Icon != project.Icon ||
                configuration.DefaultModel != project.DefaultModel ||
                configuration.DefaultThinkingLevel != project.DefaultThinkingLevel ||
                configuration.DefaultRuntimeModeId != project.DefaultRuntimeModeId ||
                configuration.AutoPullDefaultBranch != project.AutoPullDefaultBranch)
            {
                await _database.UpdateProjectConfigurationAsync(
                    project.ProjectId,
                    configuration.DefaultWorkspaceMode,
                    configuration.Scripts,
                    configuration.Icon,
                    configuration.DefaultModel,
                    configuration.DefaultThinkingLevel,
                    configuration.DefaultRuntimeModeId,
                    configuration.AutoPullDefaultBranch,
                    cancellationToken).ConfigureAwait(false);
                refreshed.Add(project with
                {
                    DefaultWorkspaceMode = configuration.DefaultWorkspaceMode,
                    Scripts = configuration.Scripts,
                    Icon = configuration.Icon,
                    DefaultModel = configuration.DefaultModel,
                    DefaultThinkingLevel = configuration.DefaultThinkingLevel,
                    DefaultRuntimeModeId = configuration.DefaultRuntimeModeId,
                    AutoPullDefaultBranch = configuration.AutoPullDefaultBranch,
                });
            }
            else
            {
                refreshed.Add(project);
            }
        }

        for (var index = 0; index < refreshed.Count; index++)
            refreshed[index] = refreshed[index] with { RepositoryKey = await ProjectRepositoryIdentity.ReadAsync(refreshed[index].CanonicalPath, cancellationToken).ConfigureAwait(false) };
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

    public async Task RemoveAsync(RemoveProjectRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            await _database.DeleteProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false);
        }
        catch (KeyNotFoundException exception)
        {
            throw new HostOperationException(ProtocolErrorCodes.ProjectNotFound, exception.Message);
        }
    }

    public async Task<ProjectDescriptor> UpdateDefaultsAsync(
        UpdateProjectDefaultsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var updateIcon = request.UpdateCustomization && request.UpdateIcon;
        var project = await _database.GetProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new HostOperationException(
                ProtocolErrorCodes.ProjectNotFound,
                $"Project '{request.ProjectId}' was not found.");
        if (!PiPermissionModes.IsSupported(request.DefaultRuntimeModeId))
        {
            throw new HostOperationException(ProtocolErrorCodes.PiConfigurationUnsupported,
                "Pi does not support arbitrary runtime mode IDs. Clear the runtime mode to use Pi defaults.");
        }
        if (request.UpdateCustomization)
        {
            if (request.Scripts?.Count > 50 || (request.Scripts ?? []).Any(script =>
                    string.IsNullOrWhiteSpace(script.Id) || script.Id.Length > 128 ||
                    string.IsNullOrWhiteSpace(script.Name) || script.Name.Length > 200 ||
                    string.IsNullOrWhiteSpace(script.Command) || script.Command.Length > 32768 || !Enum.IsDefined(script.Icon)) ||
                (request.Scripts ?? []).Select(script => script.Id).Distinct(StringComparer.Ordinal).Count() != (request.Scripts?.Count ?? 0))
                throw new ArgumentException("Use up to 50 named scripts with unique IDs and valid commands.");
            if (updateIcon && request.Icon is { Length: > 0 } icon &&
                !(icon.StartsWith("emoji:", StringComparison.Ordinal) && icon.Length <= 40) &&
                (!Path.IsPathFullyQualified(icon) || !File.Exists(icon) ||
                 !new[] { ".png", ".jpg", ".jpeg", ".ico", ".webp", ".gif" }.Contains(Path.GetExtension(icon), StringComparer.OrdinalIgnoreCase)))
                throw new ArgumentException("Choose a local image or an emoji for the project icon.");
        }
        else if (await _database.GetProjectDefaultsOverrideAsync(request.ProjectId, cancellationToken).ConfigureAwait(false) is { UpdateCustomization: true } previous)
            request = request with { Scripts = previous.Scripts, Icon = previous.Icon, UpdateCustomization = true, UpdateIcon = previous.UpdateIcon };
        if (updateIcon)
            await _database.SetProjectIconsAsync([request.ProjectId], request.Icon, cancellationToken).ConfigureAwait(false);
        await _database.SaveProjectDefaultsOverrideAsync(request, cancellationToken).ConfigureAwait(false);
        await _database.UpdateProjectConfigurationAsync(
            request.ProjectId,
            request.DefaultWorkspaceMode,
            request.UpdateCustomization ? request.Scripts ?? [] : project.Scripts ?? [],
            updateIcon ? request.Icon : project.Icon,
            request.DefaultModel,
            request.DefaultThinkingLevel,
            request.DefaultRuntimeModeId,
            request.AutoPullDefaultBranch,
            cancellationToken).ConfigureAwait(false);
        return (await _database.GetProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false))!;
    }

    private async Task<ProjectConfiguration> ApplyOverridesAsync(ProjectId projectId,
        ProjectConfiguration configuration, CancellationToken cancellationToken)
    {
        var saved = await _database.GetProjectDefaultsOverrideAsync(projectId, cancellationToken).ConfigureAwait(false);
        var effective = saved is null ? configuration with { DefaultRuntimeModeId = PiPermissionModes.IsSupported(configuration.DefaultRuntimeModeId) ? configuration.DefaultRuntimeModeId : null } : configuration with
        {
            DefaultWorkspaceMode = saved.DefaultWorkspaceMode,
            DefaultModel = saved.DefaultModel,
            DefaultThinkingLevel = saved.DefaultThinkingLevel,
            DefaultRuntimeModeId = PiPermissionModes.IsSupported(saved.DefaultRuntimeModeId) ? saved.DefaultRuntimeModeId : null,
            AutoPullDefaultBranch = saved.AutoPullDefaultBranch,
            Scripts = saved.UpdateCustomization ? saved.Scripts ?? [] : configuration.Scripts,
            Icon = saved.UpdateCustomization && saved.UpdateIcon ? saved.Icon ?? configuration.Icon : configuration.Icon,
        };
        var icon = await _database.GetProjectIconOverrideAsync(projectId, cancellationToken).ConfigureAwait(false);
        return icon.IsSet ? effective with { Icon = icon.Icon ?? configuration.Icon } : effective;
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
        var model = project.DefaultModel ?? request.InheritedModel;
        var thinking = project.DefaultThinkingLevel ??
            (project.DefaultModel is null || project.DefaultModel == request.InheritedModel ? request.InheritedThinkingLevel : null);
        if (model is not null || thinking is not null ||
            !string.IsNullOrWhiteSpace(project.DefaultRuntimeModeId))
        {
            _ = await _database.GetOrCreateThreadPiConfigurationAsync(thread.ThreadId, cancellationToken)
                .ConfigureAwait(false);
            _ = await _database.UpdateThreadPiConfigurationAsync(
                thread.ThreadId,
                0,
                model,
                thinking,
                project.DefaultRuntimeModeId,
                cancellationToken).ConfigureAwait(false);
        }

        return await _database.EnrichThreadDescriptorAsync(thread, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ThreadDescriptor>> ListThreadsAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        var threads = await _database.ListThreadsAsync(projectId, cancellationToken).ConfigureAwait(false);
        var descriptors = new List<ThreadDescriptor>(threads.Count);
        foreach (var thread in threads)
        {
            descriptors.Add(await _database.EnrichThreadDescriptorAsync(thread, cancellationToken).ConfigureAwait(false));
        }

        return ThreadOrdering.Apply(descriptors).ToArray();
    }

    public async Task<SearchThreadsResult> SearchThreadsAsync(
        SearchThreadsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Offset < 0 || request.Limit is < 1 or > ThreadLifecycleDefaults.MaximumSearchLimit)
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
        var threads = await _database.SearchThreadsPageAsync(
            request.ProjectId,
            query,
            request.IncludeArchived,
            request.Limit + 1,
            request.Offset,
            cancellationToken).ConfigureAwait(false);
        var descriptors = new List<ThreadDescriptor>();
        foreach (var thread in threads.Take(request.Limit))
        {
            descriptors.Add(await _database.EnrichThreadDescriptorAsync(thread, cancellationToken).ConfigureAwait(false));
        }

        return new SearchThreadsResult(descriptors, threads.Count > request.Limit,
            threads.Count > request.Limit ? request.Offset + request.Limit : null);
    }
}
