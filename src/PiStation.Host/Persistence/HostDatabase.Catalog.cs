using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Persistence;

public sealed partial class HostDatabase
{
    private const int CatalogRetention = 4096;
    private readonly string _catalogEpoch = Guid.NewGuid().ToString("N");

    private static async Task InitializeCatalogAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var schema = connection.CreateCommand();
        schema.CommandText = """
            CREATE TABLE IF NOT EXISTS CatalogChanges (
                Sequence INTEGER PRIMARY KEY AUTOINCREMENT, Kind TEXT NOT NULL, EntityId TEXT NOT NULL);
            """;
        await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        // SQLite triggers record changes in the writer's transaction, including non-RPC host work.
        foreach (var (table, key, kind) in new[] { ("Projects", "ProjectId", "project"), ("Threads", "ThreadId", "thread") })
        foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            var row = operation == "DELETE" ? "OLD" : "NEW";
            schema.CommandText = $"""
                CREATE TRIGGER IF NOT EXISTS Catalog_{table}_{operation} AFTER {operation} ON {table}
                BEGIN
                    INSERT INTO CatalogChanges(Kind, EntityId) VALUES ('{kind}', {row}.{key});
                    DELETE FROM CatalogChanges WHERE Sequence <= last_insert_rowid() - {CatalogRetention};
                END;
                """;
            await schema.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<CatalogBatch> ReadCatalogAsync(CatalogCursor? cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(Sequence), 0), COALESCE(MIN(Sequence), 0) FROM CatalogChanges;";
        long high, low;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            high = reader.GetInt64(0);
            low = reader.GetInt64(1);
        }
        var reset = cursor is null || cursor.Epoch != _catalogEpoch || cursor.Sequence > high || cursor.Sequence < low - 1;
        if (!reset && cursor!.Sequence == high) yield break;
        CatalogBatch Page(ProjectDescriptor[]? projects = null, ThreadDescriptor[]? threads = null,
            ProjectId[]? removedProjects = null, ThreadId[]? removedThreads = null, bool start = false, bool complete = false) =>
            new(EnvironmentId, _catalogEpoch, high, start, complete, projects ?? [], threads ?? [], removedProjects ?? [], removedThreads ?? []);

        if (reset)
        {
            yield return Page(start: true);
            command.CommandText = """
                SELECT ProjectId, CanonicalPath, DisplayName, CreatedUtc, DefaultWorkspaceMode, ScriptsJson, AreRepositoryScriptsTrusted
                FROM Projects ORDER BY ProjectId;
                """;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                var page = new List<ProjectDescriptor>(128);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    page.Add(ReadProject(reader));
                    if (page.Count == 128) { yield return Page(projects: [.. page]); page.Clear(); }
                }
                if (page.Count > 0) yield return Page(projects: [.. page]);
            }
            command.CommandText = """
                SELECT ThreadId, ProjectId, PiSessionId, PiSessionFile, Title, Revision, IsArchived, IsPinned,
                       CreatedUtc, UpdatedUtc, WorkspaceMode, BranchName, WorktreePath, WorkspaceGeneration, SetupScriptState, SetupScriptMessage
                FROM Threads ORDER BY ThreadId;
                """;
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                var page = new List<ThreadDescriptor>(128);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    page.Add(ReadThread(reader).ToDescriptor(EnvironmentId));
                    if (page.Count == 128) { yield return Page(threads: [.. page]); page.Clear(); }
                }
                if (page.Count > 0) yield return Page(threads: [.. page]);
            }
        }
        else
        {
            command.CommandText = "SELECT DISTINCT Kind, EntityId FROM CatalogChanges WHERE Sequence > $cursor ORDER BY Kind, EntityId;";
            command.Parameters.AddWithValue("$cursor", cursor!.Sequence);
            var changes = new List<(string Kind, string Id)>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) changes.Add((reader.GetString(0), reader.GetString(1)));
            command.Parameters.Clear();
            foreach (var (kind, id) in changes)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$id", id);
                if (kind == "project")
                {
                    command.CommandText = """
                        SELECT ProjectId, CanonicalPath, DisplayName, CreatedUtc, DefaultWorkspaceMode, ScriptsJson, AreRepositoryScriptsTrusted
                        FROM Projects WHERE ProjectId = $id;
                        """;
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    yield return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                        ? Page(projects: [ReadProject(reader)]) : Page(removedProjects: [new ProjectId(id)]);
                }
                else
                {
                    command.CommandText = """
                        SELECT ThreadId, ProjectId, PiSessionId, PiSessionFile, Title, Revision, IsArchived, IsPinned,
                               CreatedUtc, UpdatedUtc, WorkspaceMode, BranchName, WorktreePath, WorkspaceGeneration, SetupScriptState, SetupScriptMessage
                        FROM Threads WHERE ThreadId = $id;
                        """;
                    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    yield return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                        ? Page(threads: [ReadThread(reader).ToDescriptor(EnvironmentId)]) : Page(removedThreads: [new ThreadId(id)]);
                }
            }
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        yield return Page(complete: true);
    }
}
