using System.Text.Json;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Host.Persistence;

public sealed partial class HostDatabase
{
    public async Task<UpdateProjectDefaultsRequest?> GetProjectDefaultsOverrideAsync(
        ProjectId projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ConfigurationJson FROM ProjectDefaultsOverrides WHERE ProjectId = $id";
        command.Parameters.AddWithValue("$id", projectId.ToString());
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string json
            ? JsonSerializer.Deserialize<UpdateProjectDefaultsRequest>(json)
            : null;
    }

    public async Task SaveProjectDefaultsOverrideAsync(
        UpdateProjectDefaultsRequest request, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ProjectDefaultsOverrides(ProjectId, ConfigurationJson) VALUES($id, $json)
            ON CONFLICT(ProjectId) DO UPDATE SET ConfigurationJson = excluded.ConfigurationJson
            """;
        command.Parameters.AddWithValue("$id", request.ProjectId.ToString());
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(request));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
