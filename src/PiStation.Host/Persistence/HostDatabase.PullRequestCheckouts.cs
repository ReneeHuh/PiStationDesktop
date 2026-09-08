using System.Text.Json;
using Microsoft.Data.Sqlite;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Persistence;

public sealed partial class HostDatabase
{
    // The reservation outlives a deleted thread: a retry must never resurrect it.
    internal async Task<(ThreadId ThreadId, bool Completed)> ReservePullRequestCheckoutAsync(
        ProjectId projectId, string repository, string number, string head, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO PullRequestCheckouts (ProjectId, Repository, Number, HeadCommitId, ThreadId)
            VALUES ($project, $repo, $number, $head, $thread);
            SELECT ThreadId, Completed FROM PullRequestCheckouts
            WHERE ProjectId=$project AND Repository=$repo AND Number=$number AND HeadCommitId=$head;
            """;
        command.Parameters.AddWithValue("$project", projectId.Value);
        command.Parameters.AddWithValue("$repo", repository);
        command.Parameters.AddWithValue("$number", number);
        command.Parameters.AddWithValue("$head", head.ToLowerInvariant());
        command.Parameters.AddWithValue("$thread", ThreadId.New().Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("The review checkout reservation could not be read.");
        return (ThreadId.Parse(reader.GetString(0)), reader.GetBoolean(1));
    }

    internal async Task<HostThreadRecord> CompletePullRequestCheckoutAsync(
        ThreadId threadId, ProjectDescriptor project, string title, string branch, string path,
        PullRequestLink link, string prompt, PiModelSelection? inheritedModel,
        PiThinkingLevel? inheritedThinking, CancellationToken cancellationToken)
    {
        var model = project.DefaultModel ?? inheritedModel;
        var thinking = project.DefaultThinkingLevel ??
            (project.DefaultModel is null || project.DefaultModel == inheritedModel ? inheritedThinking : null);
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Threads (ThreadId, ProjectId, PiSessionId, PiSessionFile, Title, Revision,
                IsArchived, IsPinned, CreatedUtc, UpdatedUtc, WorkspaceMode, BranchName, WorktreePath,
                WorkspaceGeneration, SetupScriptState, SetupScriptMessage)
            VALUES ($id, $project, $id, NULL, $title, 0, 0, 0, $now, $now, 'Worktree', $branch, $path, 1, 'None', NULL);
            INSERT INTO ThreadInboxMetadata (ThreadId, IsSettled, SnoozedUntilUtc, PinnedOrder, TitleKind, PullRequestJson)
            VALUES ($id, 0, NULL, NULL, 'Manual', $link);
            INSERT INTO ThreadPiConfigurations (ThreadId, ModelProvider, ModelId, ThinkingLevel, RuntimeModeId, Revision, UpdatedUtc)
            VALUES ($id, $provider, $model, $thinking, $runtime, 0, $now);
            INSERT INTO ThreadDrafts (ThreadId, DraftId, DraftText, ContextJson, Revision, UpdatedUtc)
            VALUES ($id, $draft, $prompt, '[]', 0, $now);
            UPDATE PullRequestCheckouts SET Completed=1 WHERE ThreadId=$id;
            """;
        command.Parameters.AddWithValue("$id", threadId.Value);
        command.Parameters.AddWithValue("$project", project.ProjectId.Value);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$branch", branch);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$now", FormatDate(now));
        command.Parameters.AddWithValue("$link", JsonSerializer.Serialize(link, ProtocolJsonContext.Default.PullRequestLink));
        command.Parameters.AddWithValue("$provider", (object?)model?.ProviderId ?? DBNull.Value);
        command.Parameters.AddWithValue("$model", (object?)model?.ModelId ?? DBNull.Value);
        command.Parameters.AddWithValue("$thinking", (object?)thinking?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$runtime", (object?)project.DefaultRuntimeModeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$draft", DraftId.New().Value);
        command.Parameters.AddWithValue("$prompt", prompt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(threadId, project.ProjectId, threadId.Value, null, title, 0, false, false, now, now,
            ThreadWorkspaceMode.Worktree, branch, path, WorkspaceGeneration: 1);
    }
}
