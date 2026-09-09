using PiStation.Host.Errors;
using PiStation.Host.Projects;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Models;

namespace PiStation.Host;

public sealed partial class EnvironmentService
{
    public async Task<ProjectDescriptor[]> UpdateProjectIconsAsync(UpdateProjectIconsRequest request, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ProjectIds is null || request.ProjectIds.Count is < 1 or > 128 ||
            request.ProjectIds.Distinct().Count() != request.ProjectIds.Count)
            throw new ArgumentException("Select between one and 128 distinct project checkouts.");
        string? repository = null;
        foreach (var id in request.ProjectIds)
        {
            var project = await _database.GetProjectAsync(id, token).ConfigureAwait(false)
                ?? throw new HostOperationException(ProtocolErrorCodes.ProjectNotFound, "A selected checkout no longer exists. Refresh the project group.");
            if (request.ProjectIds.Count > 1)
            {
                var key = await ProjectRepositoryIdentity.ReadAsync(project.CanonicalPath, token).ConfigureAwait(false);
                if (key is null || (repository is not null && !string.Equals(repository, key, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("The selected checkouts no longer belong to the same repository. Refresh the project group.");
                repository = key;
            }
        }

        var icon = string.IsNullOrWhiteSpace(request.Icon) ? null : request.Icon.Trim();
        if (request.UploadedIcon is { } upload)
            icon = await ProjectIconStorage.SaveAsync(_options.CanonicalDataRoot, upload, token).ConfigureAwait(false);
        else if (icon is not null)
        {
            if (icon.StartsWith("emoji:", StringComparison.Ordinal))
            {
                if (icon.Length is <= 6 or > 40 || icon.Any(char.IsControl)) throw new ArgumentException("Choose a short, single-line emoji.");
            }
            else
            {
                if (!Path.IsPathFullyQualified(icon)) throw new ArgumentException("Choose an image on the host or upload one.");
                var content = await ProjectIconStorage.ReadAsync(icon, token).ConfigureAwait(false);
                icon = await ProjectIconStorage.SaveAsync(_options.CanonicalDataRoot, new(Path.GetFileName(icon), content), token).ConfigureAwait(false);
            }
        }
        await _database.SetProjectIconsAsync(request.ProjectIds, icon, token).ConfigureAwait(false);
        // Automatic is resolved independently in each checkout (including after restart).
        var refreshed = await _projects.ListAsync(token).ConfigureAwait(false);
        return refreshed.Where(project => request.ProjectIds.Contains(project.ProjectId)).ToArray();
    }
}
