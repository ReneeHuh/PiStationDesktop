using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;
using PiStation.Protocol.Serialization;

namespace PiStation.Host.Persistence;

public sealed class HostDatabase
{
    private readonly string _connectionString;
    private readonly HostOptions _options;
    private EnvironmentId? _environmentId;

    public HostDatabase(HostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        Directory.CreateDirectory(options.CanonicalDataRoot);
        Directory.CreateDirectory(options.SessionRoot);
        Directory.CreateDirectory(options.AttachmentRoot);
        Directory.CreateDirectory(options.AttachmentStagingRoot);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = false,
        }.ToString();
    }

    public EnvironmentId EnvironmentId => _environmentId ??
        throw new InvalidOperationException("Initialize the host database before using environment-owned records.");

    public async Task<HostEnvironmentRecord> InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA busy_timeout = 5000;

                CREATE TABLE IF NOT EXISTS Environment (
                    EnvironmentId TEXT PRIMARY KEY NOT NULL,
                    Name TEXT NOT NULL,
                    CreatedUtc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS Projects (
                    ProjectId TEXT PRIMARY KEY NOT NULL,
                    CanonicalPath TEXT NOT NULL COLLATE NOCASE UNIQUE,
                    DisplayName TEXT NOT NULL,
                    DefaultWorkspaceMode TEXT NOT NULL DEFAULT 'Local',
                    ScriptsJson TEXT NOT NULL DEFAULT '[]',
                    AreRepositoryScriptsTrusted INTEGER NOT NULL DEFAULT 0 CHECK (AreRepositoryScriptsTrusted IN (0, 1)),
                    CreatedUtc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS Threads (
                    ThreadId TEXT PRIMARY KEY NOT NULL,
                    ProjectId TEXT NOT NULL,
                    PiSessionId TEXT NOT NULL,
                    PiSessionFile TEXT NULL,
                    Title TEXT NOT NULL,
                    Revision INTEGER NOT NULL DEFAULT 0,
                    IsArchived INTEGER NOT NULL DEFAULT 0 CHECK (IsArchived IN (0, 1)),
                    IsPinned INTEGER NOT NULL DEFAULT 0 CHECK (IsPinned IN (0, 1)),
                    WorkspaceMode TEXT NOT NULL DEFAULT 'Local',
                    BranchName TEXT NULL,
                    WorktreePath TEXT NULL,
                    WorkspaceGeneration INTEGER NOT NULL DEFAULT 0,
                    SetupScriptState TEXT NOT NULL DEFAULT 'None',
                    SetupScriptMessage TEXT NULL,
                    CreatedUtc TEXT NOT NULL,
                    UpdatedUtc TEXT NOT NULL,
                    FOREIGN KEY (ProjectId) REFERENCES Projects(ProjectId) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS IX_Threads_ProjectId ON Threads(ProjectId);

                CREATE TABLE IF NOT EXISTS ThreadPiConfigurations (
                    ThreadId TEXT PRIMARY KEY NOT NULL,
                    ModelProvider TEXT NULL,
                    ModelId TEXT NULL,
                    ThinkingLevel TEXT NULL,
                    RuntimeModeId TEXT NULL,
                    Revision INTEGER NOT NULL,
                    UpdatedUtc TEXT NOT NULL,
                    CHECK (
                        (ModelProvider IS NULL AND ModelId IS NULL) OR
                        (ModelProvider IS NOT NULL AND ModelId IS NOT NULL)
                    ),
                    FOREIGN KEY (ThreadId) REFERENCES Threads(ThreadId) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS ThreadDrafts (
                    ThreadId TEXT PRIMARY KEY NOT NULL,
                    DraftId TEXT NOT NULL UNIQUE,
                    DraftText TEXT NOT NULL,
                    Revision INTEGER NOT NULL,
                    UpdatedUtc TEXT NOT NULL,
                    FOREIGN KEY (ThreadId) REFERENCES Threads(ThreadId) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS DraftAttachments (
                    AttachmentId TEXT PRIMARY KEY NOT NULL,
                    DraftId TEXT NOT NULL,
                    ThreadId TEXT NOT NULL,
                    Ordinal INTEGER NOT NULL,
                    FileName TEXT NOT NULL,
                    MediaType TEXT NOT NULL,
                    ByteLength INTEGER NOT NULL,
                    Sha256 TEXT NOT NULL,
                    ServerPath TEXT NOT NULL,
                    CreatedUtc TEXT NOT NULL,
                    UNIQUE (DraftId, Ordinal),
                    FOREIGN KEY (DraftId) REFERENCES ThreadDrafts(DraftId) ON DELETE CASCADE,
                    FOREIGN KEY (ThreadId) REFERENCES Threads(ThreadId) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS IX_DraftAttachments_DraftId
                ON DraftAttachments(DraftId, Ordinal);

                CREATE TABLE IF NOT EXISTS CommandReceipts (
                    ClientId TEXT NOT NULL,
                    CommandId TEXT NOT NULL,
                    EnvironmentId TEXT NOT NULL,
                    ThreadId TEXT NOT NULL,
                    BodyHash TEXT NOT NULL,
                    State TEXT NOT NULL,
                    ErrorCode TEXT NULL,
                    CreatedUtc TEXT NOT NULL,
                    UpdatedUtc TEXT NOT NULL,
                    PRIMARY KEY (ClientId, CommandId),
                    FOREIGN KEY (EnvironmentId) REFERENCES Environment(EnvironmentId),
                    FOREIGN KEY (ThreadId) REFERENCES Threads(ThreadId) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS ThreadCheckpoints (
                    ThreadId TEXT NOT NULL,
                    TurnId TEXT NOT NULL,
                    TurnCount INTEGER NOT NULL CHECK (TurnCount > 0),
                    CheckpointRef TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    FilesJson TEXT NOT NULL,
                    PiEntryIdBefore TEXT NULL,
                    PiEntryId TEXT NULL,
                    CompletedUtc TEXT NOT NULL,
                    BeforeCheckpointRef TEXT NULL,
                    WorkspaceGeneration INTEGER NOT NULL DEFAULT 0,
                    BranchName TEXT NULL,
                    HeadShaBefore TEXT NULL,
                    HeadShaAfter TEXT NULL,
                    PRIMARY KEY (ThreadId, TurnCount),
                    FOREIGN KEY (ThreadId) REFERENCES Threads(ThreadId) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS IX_ThreadCheckpoints_ThreadTurn
                ON ThreadCheckpoints(ThreadId, TurnCount);

                CREATE TABLE IF NOT EXISTS WorkspaceCommandReceipts (
                    ClientId TEXT NOT NULL,
                    CommandId TEXT NOT NULL,
                    EnvironmentId TEXT NOT NULL,
                    ProjectId TEXT NOT NULL,
                    ThreadId TEXT NULL,
                    BodyHash TEXT NOT NULL,
                    State TEXT NOT NULL,
                    ErrorCode TEXT NULL,
                    ResultJson TEXT NULL,
                    CreatedUtc TEXT NOT NULL,
                    UpdatedUtc TEXT NOT NULL,
                    PRIMARY KEY (ClientId, CommandId),
                    FOREIGN KEY (EnvironmentId) REFERENCES Environment(EnvironmentId),
                    FOREIGN KEY (ProjectId) REFERENCES Projects(ProjectId) ON DELETE CASCADE,
                    FOREIGN KEY (ThreadId) REFERENCES Threads(ThreadId) ON DELETE CASCADE
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await EnsureProjectConfigurationColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsureThreadCheckpointColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsureThreadLifecycleColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsureThreadWorkspaceColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        await using (var lifecycleIndex = connection.CreateCommand())
        {
            lifecycleIndex.CommandText = """
                CREATE INDEX IF NOT EXISTS IX_Threads_ProjectLifecycle
                ON Threads(ProjectId, IsArchived, IsPinned DESC, UpdatedUtc DESC, ThreadId);
                """;
            await lifecycleIndex.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var environment = await ReadEnvironmentAsync(connection, cancellationToken).ConfigureAwait(false);
        if (environment is null)
        {
            environment = new HostEnvironmentRecord(
                EnvironmentId.New(),
                _options.EnvironmentName,
                DateTimeOffset.UtcNow);
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO Environment (EnvironmentId, Name, CreatedUtc)
                VALUES ($environmentId, $name, $createdUtc);
                """;
            insert.Parameters.AddWithValue("$environmentId", environment.EnvironmentId.Value);
            insert.Parameters.AddWithValue("$name", environment.Name);
            insert.Parameters.AddWithValue("$createdUtc", FormatDate(environment.CreatedUtc));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var recover = connection.CreateCommand())
        {
            recover.CommandText = """
                UPDATE CommandReceipts
                SET State = $uncertain, ErrorCode = $errorCode, UpdatedUtc = $updatedUtc
                WHERE State IN ($received, $dispatching);
                """;
            recover.Parameters.AddWithValue("$uncertain", CommandReceiptState.DispatchUncertain.ToString());
            recover.Parameters.AddWithValue("$errorCode", "DispatchUncertain");
            recover.Parameters.AddWithValue("$updatedUtc", FormatDate(DateTimeOffset.UtcNow));
            recover.Parameters.AddWithValue("$received", CommandReceiptState.Received.ToString());
            recover.Parameters.AddWithValue("$dispatching", CommandReceiptState.Dispatching.ToString());
            await recover.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var recoverWorkspace = connection.CreateCommand())
        {
            recoverWorkspace.CommandText = """
                UPDATE WorkspaceCommandReceipts
                SET State = $uncertain, ErrorCode = $errorCode, UpdatedUtc = $updatedUtc
                WHERE State IN ($received, $dispatching);
                """;
            recoverWorkspace.Parameters.AddWithValue("$uncertain", CommandReceiptState.DispatchUncertain.ToString());
            recoverWorkspace.Parameters.AddWithValue(
                "$errorCode",
                PiStation.Protocol.Errors.ProtocolErrorCodes.DispatchUncertain);
            recoverWorkspace.Parameters.AddWithValue("$updatedUtc", FormatDate(DateTimeOffset.UtcNow));
            recoverWorkspace.Parameters.AddWithValue("$received", CommandReceiptState.Received.ToString());
            recoverWorkspace.Parameters.AddWithValue("$dispatching", CommandReceiptState.Dispatching.ToString());
            await recoverWorkspace.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        _environmentId = environment.EnvironmentId;
        return environment;
    }

    public async Task<ProjectDescriptor> AddProjectAsync(
        string canonicalPath,
        string displayName,
        ThreadWorkspaceMode defaultWorkspaceMode = ThreadWorkspaceMode.Local,
        IReadOnlyList<ProjectScript>? scripts = null,
        CancellationToken cancellationToken = default)
    {
        var created = new ProjectDescriptor(
            EnvironmentId,
            ProjectId.New(),
            canonicalPath,
            displayName,
            DateTimeOffset.UtcNow,
            defaultWorkspaceMode,
            scripts ?? []);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT OR IGNORE INTO Projects
                    (ProjectId, CanonicalPath, DisplayName, DefaultWorkspaceMode, ScriptsJson, CreatedUtc)
                VALUES ($projectId, $path, $name, $workspaceMode, $scriptsJson, $createdUtc);
                """;
            insert.Parameters.AddWithValue("$projectId", created.ProjectId.Value);
            insert.Parameters.AddWithValue("$path", canonicalPath);
            insert.Parameters.AddWithValue("$name", displayName);
            insert.Parameters.AddWithValue("$workspaceMode", defaultWorkspaceMode.ToString());
            insert.Parameters.AddWithValue(
                "$scriptsJson",
                JsonSerializer.Serialize((scripts ?? []).ToArray(), ProtocolJsonContext.Default.ProjectScriptArray));
            insert.Parameters.AddWithValue("$createdUtc", FormatDate(created.CreatedUtc));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT ProjectId, CanonicalPath, DisplayName, CreatedUtc,
                   DefaultWorkspaceMode, ScriptsJson, AreRepositoryScriptsTrusted
            FROM Projects WHERE CanonicalPath = $path COLLATE NOCASE;
            """;
        select.Parameters.AddWithValue("$path", canonicalPath);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadProject(reader)
            : throw new InvalidOperationException("The project was not persisted.");
    }

    public async Task<IReadOnlyList<ProjectDescriptor>> ListProjectsAsync(
        CancellationToken cancellationToken = default)
    {
        var projects = new List<ProjectDescriptor>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ProjectId, CanonicalPath, DisplayName, CreatedUtc,
                   DefaultWorkspaceMode, ScriptsJson, AreRepositoryScriptsTrusted
            FROM Projects ORDER BY DisplayName, ProjectId;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            projects.Add(ReadProject(reader));
        }

        return projects;
    }

    public async Task<ProjectDescriptor?> GetProjectAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ProjectId, CanonicalPath, DisplayName, CreatedUtc,
                   DefaultWorkspaceMode, ScriptsJson, AreRepositoryScriptsTrusted
            FROM Projects WHERE ProjectId = $projectId;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadProject(reader) : null;
    }

    public async Task UpdateProjectConfigurationAsync(
        ProjectId projectId,
        ThreadWorkspaceMode defaultWorkspaceMode,
        IReadOnlyList<ProjectScript> scripts,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Projects
            SET DefaultWorkspaceMode = $workspaceMode, ScriptsJson = $scriptsJson
            WHERE ProjectId = $projectId;
            """;
        command.Parameters.AddWithValue("$workspaceMode", defaultWorkspaceMode.ToString());
        command.Parameters.AddWithValue(
            "$scriptsJson",
            JsonSerializer.Serialize(scripts.ToArray(), ProtocolJsonContext.Default.ProjectScriptArray));
        command.Parameters.AddWithValue("$projectId", projectId.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new KeyNotFoundException($"Project '{projectId}' was not found.");
        }
    }

    public async Task<ProjectDescriptor> SetProjectScriptsTrustAsync(
        ProjectId projectId,
        bool isTrusted,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE Projects SET AreRepositoryScriptsTrusted = $trusted
                WHERE ProjectId = $projectId;
                """;
            command.Parameters.AddWithValue("$trusted", isTrusted ? 1 : 0);
            command.Parameters.AddWithValue("$projectId", projectId.Value);
            if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new KeyNotFoundException($"Project '{projectId}' was not found.");
            }
        }

        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT ProjectId, CanonicalPath, DisplayName, CreatedUtc,
                   DefaultWorkspaceMode, ScriptsJson, AreRepositoryScriptsTrusted
            FROM Projects WHERE ProjectId = $projectId;
            """;
        select.Parameters.AddWithValue("$projectId", projectId.Value);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadProject(reader)
            : throw new KeyNotFoundException($"Project '{projectId}' was not found.");
    }

    public async Task<HostThreadRecord> CreateThreadAsync(
        ProjectId projectId,
        string title,
        ThreadWorkspaceMode workspaceMode = ThreadWorkspaceMode.Local,
        string? branchName = null,
        string? worktreePath = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var threadId = ThreadId.New();
        var record = new HostThreadRecord(
            threadId,
            projectId,
            threadId.Value,
            null,
            title,
            0,
            false,
            false,
            now,
            now,
            workspaceMode,
            branchName,
            worktreePath);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Threads
                (ThreadId, ProjectId, PiSessionId, PiSessionFile, Title,
                 Revision, IsArchived, IsPinned, CreatedUtc, UpdatedUtc,
                 WorkspaceMode, BranchName, WorktreePath, WorkspaceGeneration,
                 SetupScriptState, SetupScriptMessage)
            VALUES
                ($threadId, $projectId, $sessionId, NULL, $title,
                 $revision, $isArchived, $isPinned, $createdUtc, $updatedUtc,
                 $workspaceMode, $branchName, $worktreePath, 0, 'None', NULL);
            """;
        command.Parameters.AddWithValue("$threadId", record.ThreadId.Value);
        command.Parameters.AddWithValue("$projectId", record.ProjectId.Value);
        command.Parameters.AddWithValue("$sessionId", record.PiSessionId);
        command.Parameters.AddWithValue("$title", record.Title);
        command.Parameters.AddWithValue("$revision", record.Revision);
        command.Parameters.AddWithValue("$isArchived", record.IsArchived ? 1 : 0);
        command.Parameters.AddWithValue("$isPinned", record.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue("$createdUtc", FormatDate(record.CreatedUtc));
        command.Parameters.AddWithValue("$updatedUtc", FormatDate(record.UpdatedUtc));
        command.Parameters.AddWithValue("$workspaceMode", record.WorkspaceMode.ToString());
        command.Parameters.AddWithValue("$branchName", (object?)record.BranchName ?? DBNull.Value);
        command.Parameters.AddWithValue("$worktreePath", (object?)record.WorktreePath ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return record;
    }

    public async Task<IReadOnlyList<HostThreadRecord>> ListThreadsAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
        => await ListThreadsAsync(projectId, includeArchived: false, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<HostThreadRecord>> ListThreadsAsync(
        ProjectId projectId,
        bool includeArchived,
        CancellationToken cancellationToken = default)
    {
        var threads = new List<HostThreadRecord>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ThreadId, ProjectId, PiSessionId, PiSessionFile, Title,
                   Revision, IsArchived, IsPinned, CreatedUtc, UpdatedUtc,
                   WorkspaceMode, BranchName, WorktreePath, WorkspaceGeneration,
                   SetupScriptState, SetupScriptMessage
            FROM Threads
            WHERE ProjectId = $projectId AND ($includeArchived = 1 OR IsArchived = 0)
            ORDER BY IsArchived, IsPinned DESC, UpdatedUtc DESC, ThreadId;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.Value);
        command.Parameters.AddWithValue("$includeArchived", includeArchived ? 1 : 0);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            threads.Add(ReadThread(reader));
        }

        return threads;
    }

    public async Task<HostThreadRecord?> GetThreadAsync(
        ThreadId threadId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ThreadId, ProjectId, PiSessionId, PiSessionFile, Title,
                   Revision, IsArchived, IsPinned, CreatedUtc, UpdatedUtc,
                   WorkspaceMode, BranchName, WorktreePath, WorkspaceGeneration,
                   SetupScriptState, SetupScriptMessage
            FROM Threads WHERE ThreadId = $threadId;
            """;
        command.Parameters.AddWithValue("$threadId", threadId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadThread(reader) : null;
    }

    public async Task<IReadOnlyList<HostThreadRecord>> SearchThreadsAsync(
        ProjectId projectId,
        string query,
        bool includeArchived,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var threads = new List<HostThreadRecord>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ThreadId, ProjectId, PiSessionId, PiSessionFile, Title,
                   Revision, IsArchived, IsPinned, CreatedUtc, UpdatedUtc,
                   WorkspaceMode, BranchName, WorktreePath, WorkspaceGeneration,
                   SetupScriptState, SetupScriptMessage
            FROM Threads
            WHERE ProjectId = $projectId
              AND ($includeArchived = 1 OR IsArchived = 0)
              AND ($query = '' OR Title LIKE $pattern ESCAPE '\' COLLATE NOCASE)
            ORDER BY IsArchived, IsPinned DESC, UpdatedUtc DESC, ThreadId
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.Value);
        command.Parameters.AddWithValue("$includeArchived", includeArchived ? 1 : 0);
        command.Parameters.AddWithValue("$query", query);
        command.Parameters.AddWithValue("$pattern", $"%{EscapeLikePattern(query)}%");
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            threads.Add(ReadThread(reader));
        }

        return threads;
    }

    public async Task<ThreadMetadataUpdateResult> UpdateThreadMetadataAsync(
        ThreadId threadId,
        long expectedRevision,
        string? title,
        bool? isArchived,
        bool? isPinned,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var update = connection.CreateCommand())
        {
            update.CommandText = """
                UPDATE Threads
                SET Title = COALESCE($title, Title),
                    IsArchived = COALESCE($isArchived, IsArchived),
                    IsPinned = COALESCE($isPinned, IsPinned),
                    Revision = Revision + 1,
                    UpdatedUtc = $updatedUtc
                WHERE ThreadId = $threadId AND Revision = $expectedRevision;
                """;
            update.Parameters.AddWithValue("$title", (object?)title ?? DBNull.Value);
            update.Parameters.AddWithValue("$isArchived", isArchived is null ? DBNull.Value : isArchived.Value ? 1 : 0);
            update.Parameters.AddWithValue("$isPinned", isPinned is null ? DBNull.Value : isPinned.Value ? 1 : 0);
            update.Parameters.AddWithValue("$updatedUtc", FormatDate(DateTimeOffset.UtcNow));
            update.Parameters.AddWithValue("$threadId", threadId.Value);
            update.Parameters.AddWithValue("$expectedRevision", expectedRevision);
            var wasUpdated = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;

            await using var read = connection.CreateCommand();
            read.CommandText = """
                SELECT ThreadId, ProjectId, PiSessionId, PiSessionFile, Title,
                       Revision, IsArchived, IsPinned, CreatedUtc, UpdatedUtc,
                       WorkspaceMode, BranchName, WorktreePath, WorkspaceGeneration,
                       SetupScriptState, SetupScriptMessage
                FROM Threads WHERE ThreadId = $threadId;
                """;
            read.Parameters.AddWithValue("$threadId", threadId.Value);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var thread = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? ReadThread(reader)
                : null;
            return new ThreadMetadataUpdateResult(thread, wasUpdated);
        }
    }

    public Task UpdateThreadSessionFileAsync(
        ThreadId threadId,
        string? sessionFile,
        CancellationToken cancellationToken = default) =>
        UpdateThreadSessionAsync(threadId, null, sessionFile, cancellationToken);

    public async Task UpdateThreadSessionAsync(
        ThreadId threadId,
        string? sessionId,
        string? sessionFile,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Threads
            SET PiSessionId = COALESCE($sessionId, PiSessionId),
                PiSessionFile = $sessionFile,
                UpdatedUtc = $updatedUtc
            WHERE ThreadId = $threadId;
            """;
        command.Parameters.AddWithValue("$sessionId", (object?)sessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$sessionFile", (object?)sessionFile ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedUtc", FormatDate(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$threadId", threadId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateThreadWorkspaceAsync(
        ThreadId threadId,
        ThreadWorkspaceMode workspaceMode,
        string? branchName,
        string? worktreePath,
        bool incrementGeneration,
        SetupScriptState? setupScriptState = null,
        string? setupScriptMessage = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Threads
            SET WorkspaceMode = $workspaceMode,
                BranchName = $branchName,
                WorktreePath = $worktreePath,
                WorkspaceGeneration = WorkspaceGeneration + $generationIncrement,
                SetupScriptState = COALESCE($setupScriptState, SetupScriptState),
                SetupScriptMessage = CASE
                    WHEN $setupScriptState IS NULL THEN SetupScriptMessage
                    ELSE $setupScriptMessage
                END,
                UpdatedUtc = $updatedUtc
            WHERE ThreadId = $threadId;
            """;
        command.Parameters.AddWithValue("$workspaceMode", workspaceMode.ToString());
        command.Parameters.AddWithValue("$branchName", (object?)branchName ?? DBNull.Value);
        command.Parameters.AddWithValue("$worktreePath", (object?)worktreePath ?? DBNull.Value);
        command.Parameters.AddWithValue("$generationIncrement", incrementGeneration ? 1 : 0);
        command.Parameters.AddWithValue(
            "$setupScriptState",
            setupScriptState is null ? DBNull.Value : setupScriptState.Value.ToString());
        command.Parameters.AddWithValue("$setupScriptMessage", (object?)setupScriptMessage ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedUtc", FormatDate(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$threadId", threadId.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new KeyNotFoundException($"Thread '{threadId}' was not found.");
        }
    }

    public async Task DeleteThreadAsync(ThreadId threadId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Threads WHERE ThreadId = $threadId;";
        command.Parameters.AddWithValue("$threadId", threadId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ThreadCheckpoint>> ListThreadCheckpointsAsync(
        ThreadId threadId,
        CancellationToken cancellationToken = default)
    {
        var checkpoints = new List<ThreadCheckpoint>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT TurnId, TurnCount, CheckpointRef, Status, FilesJson,
                   PiEntryIdBefore, PiEntryId, CompletedUtc, BeforeCheckpointRef,
                   WorkspaceGeneration, BranchName, HeadShaBefore, HeadShaAfter
            FROM ThreadCheckpoints
            WHERE ThreadId = $threadId
            ORDER BY TurnCount;
            """;
        command.Parameters.AddWithValue("$threadId", threadId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var files = JsonSerializer.Deserialize(
                reader.GetString(4),
                ProtocolJsonContext.Default.ThreadCheckpointFileArray) ?? [];
            checkpoints.Add(new ThreadCheckpoint(
                TurnId.Parse(reader.GetString(0)),
                reader.GetInt32(1),
                reader.GetString(2),
                Enum.Parse<ThreadCheckpointStatus>(reader.GetString(3), ignoreCase: false),
                files,
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                ParseDate(reader.GetString(7)),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetInt64(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetString(12)));
        }

        return checkpoints;
    }

    public async Task UpsertThreadCheckpointAsync(
        ThreadId threadId,
        ThreadCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ThreadCheckpoints
                (ThreadId, TurnId, TurnCount, CheckpointRef, Status, FilesJson,
                 PiEntryIdBefore, PiEntryId, CompletedUtc, BeforeCheckpointRef,
                 WorkspaceGeneration, BranchName, HeadShaBefore, HeadShaAfter)
            VALUES
                ($threadId, $turnId, $turnCount, $checkpointRef, $status, $filesJson,
                 $piEntryIdBefore, $piEntryId, $completedUtc, $beforeCheckpointRef,
                 $workspaceGeneration, $branchName, $headShaBefore, $headShaAfter)
            ON CONFLICT(ThreadId, TurnCount) DO UPDATE SET
                TurnId = excluded.TurnId,
                CheckpointRef = excluded.CheckpointRef,
                Status = excluded.Status,
                FilesJson = excluded.FilesJson,
                PiEntryIdBefore = excluded.PiEntryIdBefore,
                PiEntryId = excluded.PiEntryId,
                BeforeCheckpointRef = excluded.BeforeCheckpointRef,
                WorkspaceGeneration = excluded.WorkspaceGeneration,
                BranchName = excluded.BranchName,
                HeadShaBefore = excluded.HeadShaBefore,
                HeadShaAfter = excluded.HeadShaAfter,
                CompletedUtc = excluded.CompletedUtc;
            """;
        command.Parameters.AddWithValue("$threadId", threadId.Value);
        command.Parameters.AddWithValue("$turnId", checkpoint.TurnId.Value);
        command.Parameters.AddWithValue("$turnCount", checkpoint.TurnCount);
        command.Parameters.AddWithValue("$checkpointRef", checkpoint.CheckpointRef);
        command.Parameters.AddWithValue("$status", checkpoint.Status.ToString());
        command.Parameters.AddWithValue(
            "$filesJson",
            JsonSerializer.Serialize(checkpoint.Files.ToArray(), ProtocolJsonContext.Default.ThreadCheckpointFileArray));
        command.Parameters.AddWithValue(
            "$piEntryIdBefore",
            (object?)checkpoint.PiEntryIdBeforeTurn ?? DBNull.Value);
        command.Parameters.AddWithValue("$piEntryId", (object?)checkpoint.PiEntryIdAfterTurn ?? DBNull.Value);
        command.Parameters.AddWithValue("$completedUtc", FormatDate(checkpoint.CompletedUtc));
        command.Parameters.AddWithValue(
            "$beforeCheckpointRef",
            (object?)checkpoint.BeforeCheckpointRef ?? DBNull.Value);
        command.Parameters.AddWithValue("$workspaceGeneration", checkpoint.WorkspaceGeneration);
        command.Parameters.AddWithValue("$branchName", (object?)checkpoint.BranchName ?? DBNull.Value);
        command.Parameters.AddWithValue("$headShaBefore", (object?)checkpoint.HeadShaBefore ?? DBNull.Value);
        command.Parameters.AddWithValue("$headShaAfter", (object?)checkpoint.HeadShaAfter ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> DeleteThreadCheckpointsAfterAsync(
        ThreadId threadId,
        int turnCount,
        CancellationToken cancellationToken = default)
    {
        var checkpointRefs = new List<string>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT CheckpointRef, BeforeCheckpointRef FROM ThreadCheckpoints
                WHERE ThreadId = $threadId AND TurnCount > $turnCount
                ORDER BY TurnCount;
                """;
            read.Parameters.AddWithValue("$threadId", threadId.Value);
            read.Parameters.AddWithValue("$turnCount", turnCount);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                checkpointRefs.Add(reader.GetString(0));
                if (!reader.IsDBNull(1))
                {
                    checkpointRefs.Add(reader.GetString(1));
                }
            }
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM ThreadCheckpoints
                WHERE ThreadId = $threadId AND TurnCount > $turnCount;
                """;
            delete.Parameters.AddWithValue("$threadId", threadId.Value);
            delete.Parameters.AddWithValue("$turnCount", turnCount);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return checkpointRefs;
    }

    public async Task<ThreadPiConfiguration> GetOrCreateThreadPiConfigurationAsync(
        ThreadId threadId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT OR IGNORE INTO ThreadPiConfigurations
                    (ThreadId, ModelProvider, ModelId, ThinkingLevel, RuntimeModeId, Revision, UpdatedUtc)
                SELECT ThreadId, NULL, NULL, NULL, NULL, 0, $updatedUtc
                FROM Threads
                WHERE ThreadId = $threadId;
                """;
            insert.Parameters.AddWithValue("$updatedUtc", FormatDate(DateTimeOffset.UtcNow));
            insert.Parameters.AddWithValue("$threadId", threadId.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return await ReadThreadPiConfigurationAsync(connection, threadId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Thread '{threadId}' was not found.");
    }

    public async Task<PiConfigurationUpdateResult> UpdateThreadPiConfigurationAsync(
        ThreadId threadId,
        long expectedRevision,
        PiModelSelection? model,
        PiThinkingLevel? thinkingLevel,
        string? runtimeModeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE ThreadPiConfigurations
            SET ModelProvider = $modelProvider,
                ModelId = $modelId,
                ThinkingLevel = $thinkingLevel,
                RuntimeModeId = $runtimeModeId,
                Revision = Revision + 1,
                UpdatedUtc = $updatedUtc
            WHERE ThreadId = $threadId AND Revision = $expectedRevision;
            """;
        update.Parameters.AddWithValue("$modelProvider", (object?)model?.ProviderId ?? DBNull.Value);
        update.Parameters.AddWithValue("$modelId", (object?)model?.ModelId ?? DBNull.Value);
        update.Parameters.AddWithValue("$thinkingLevel", (object?)thinkingLevel?.ToString() ?? DBNull.Value);
        update.Parameters.AddWithValue("$runtimeModeId", (object?)runtimeModeId ?? DBNull.Value);
        update.Parameters.AddWithValue("$updatedUtc", FormatDate(DateTimeOffset.UtcNow));
        update.Parameters.AddWithValue("$threadId", threadId.Value);
        update.Parameters.AddWithValue("$expectedRevision", expectedRevision);
        var wasUpdated = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        var configuration = await ReadThreadPiConfigurationAsync(connection, threadId, cancellationToken)
            .ConfigureAwait(false);
        return new PiConfigurationUpdateResult(configuration, wasUpdated);
    }

    public async Task<ThreadDraft> GetOrCreateThreadDraftAsync(
        ThreadId threadId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT OR IGNORE INTO ThreadDrafts (ThreadId, DraftId, DraftText, Revision, UpdatedUtc)
                SELECT ThreadId, $draftId, '', 0, $updatedUtc
                FROM Threads
                WHERE ThreadId = $threadId;
                """;
            insert.Parameters.AddWithValue("$draftId", DraftId.New().Value);
            insert.Parameters.AddWithValue("$updatedUtc", FormatDate(DateTimeOffset.UtcNow));
            insert.Parameters.AddWithValue("$threadId", threadId.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return await ReadThreadDraftAsync(connection, threadId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Thread '{threadId}' was not found.");
    }

    public async Task<DraftUpdateResult> UpdateThreadDraftAsync(
        ThreadId threadId,
        DraftId draftId,
        long expectedRevision,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE ThreadDrafts
            SET DraftText = $text, Revision = Revision + 1, UpdatedUtc = $updatedUtc
            WHERE ThreadId = $threadId AND DraftId = $draftId AND Revision = $expectedRevision;
            """;
        update.Parameters.AddWithValue("$text", text);
        update.Parameters.AddWithValue("$updatedUtc", FormatDate(DateTimeOffset.UtcNow));
        update.Parameters.AddWithValue("$threadId", threadId.Value);
        update.Parameters.AddWithValue("$draftId", draftId.Value);
        update.Parameters.AddWithValue("$expectedRevision", expectedRevision);
        var wasUpdated = await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        var draft = await ReadThreadDraftAsync(connection, threadId, cancellationToken).ConfigureAwait(false);
        return new DraftUpdateResult(draft, wasUpdated);
    }

    public async Task<DraftAttachmentMutationResult> AddDraftAttachmentAsync(
        ThreadId threadId,
        DraftId draftId,
        long expectedRevision,
        DraftAttachment attachment,
        int maximumAttachments,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var current = await ReadDraftHeaderAsync(connection, transaction, threadId, cancellationToken)
            .ConfigureAwait(false);
        if (current is null || current.Value.DraftId != draftId)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new DraftAttachmentMutationResult(null, DraftAttachmentMutationState.DraftNotFound);
        }

        if (current.Value.Revision != expectedRevision)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new DraftAttachmentMutationResult(
                await ReadThreadDraftAsync(connection, threadId, cancellationToken).ConfigureAwait(false),
                DraftAttachmentMutationState.DraftConflict);
        }

        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT COUNT(*) FROM DraftAttachments WHERE AttachmentId = $attachmentId;";
            existing.Parameters.AddWithValue("$attachmentId", attachment.AttachmentId.Value);
            if (Convert.ToInt64(await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                    CultureInfo.InvariantCulture) != 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new DraftAttachmentMutationResult(
                    await ReadThreadDraftAsync(connection, threadId, cancellationToken).ConfigureAwait(false),
                    DraftAttachmentMutationState.AttachmentConflict);
            }
        }

        long count;
        long nextOrdinal;
        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.Transaction = transaction;
            countCommand.CommandText = """
                SELECT COUNT(*), COALESCE(MAX(Ordinal), -1) + 1
                FROM DraftAttachments
                WHERE DraftId = $draftId;
                """;
            countCommand.Parameters.AddWithValue("$draftId", draftId.Value);
            await using var reader = await countCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The attachment count could not be read.");
            }

            count = reader.GetInt64(0);
            nextOrdinal = reader.GetInt64(1);
        }

        if (count >= maximumAttachments)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new DraftAttachmentMutationResult(
                await ReadThreadDraftAsync(connection, threadId, cancellationToken).ConfigureAwait(false),
                DraftAttachmentMutationState.AttachmentLimitExceeded);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO DraftAttachments
                    (AttachmentId, DraftId, ThreadId, Ordinal, FileName, MediaType, ByteLength,
                     Sha256, ServerPath, CreatedUtc)
                VALUES
                    ($attachmentId, $draftId, $threadId, $ordinal, $fileName, $mediaType, $byteLength,
                     $sha256, $serverPath, $createdUtc);
                """;
            insert.Parameters.AddWithValue("$attachmentId", attachment.AttachmentId.Value);
            insert.Parameters.AddWithValue("$draftId", draftId.Value);
            insert.Parameters.AddWithValue("$threadId", threadId.Value);
            insert.Parameters.AddWithValue("$ordinal", nextOrdinal);
            insert.Parameters.AddWithValue("$fileName", attachment.FileName);
            insert.Parameters.AddWithValue("$mediaType", attachment.MediaType);
            insert.Parameters.AddWithValue("$byteLength", attachment.ByteLength);
            insert.Parameters.AddWithValue("$sha256", attachment.Sha256);
            insert.Parameters.AddWithValue("$serverPath", attachment.ServerPath);
            insert.Parameters.AddWithValue("$createdUtc", FormatDate(attachment.CreatedUtc));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await IncrementDraftRevisionAsync(
            connection,
            transaction,
            threadId,
            draftId,
            expectedRevision,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new DraftAttachmentMutationResult(
            await ReadThreadDraftAsync(connection, threadId, cancellationToken).ConfigureAwait(false),
            DraftAttachmentMutationState.Updated);
    }

    public async Task<DraftAttachmentMutationResult> RemoveDraftAttachmentAsync(
        ThreadId threadId,
        DraftId draftId,
        AttachmentId attachmentId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var current = await ReadDraftHeaderAsync(connection, transaction, threadId, cancellationToken)
            .ConfigureAwait(false);
        if (current is null || current.Value.DraftId != draftId)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new DraftAttachmentMutationResult(null, DraftAttachmentMutationState.DraftNotFound);
        }

        if (current.Value.Revision != expectedRevision)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new DraftAttachmentMutationResult(
                await ReadThreadDraftAsync(connection, threadId, cancellationToken).ConfigureAwait(false),
                DraftAttachmentMutationState.DraftConflict);
        }

        DraftAttachment? removed;
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT FileName, MediaType, ByteLength, Sha256, ServerPath, CreatedUtc
                FROM DraftAttachments
                WHERE AttachmentId = $attachmentId AND DraftId = $draftId AND ThreadId = $threadId;
                """;
            select.Parameters.AddWithValue("$attachmentId", attachmentId.Value);
            select.Parameters.AddWithValue("$draftId", draftId.Value);
            select.Parameters.AddWithValue("$threadId", threadId.Value);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            removed = await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                ? new DraftAttachment(
                    EnvironmentId,
                    threadId,
                    draftId,
                    attachmentId,
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    ParseDate(reader.GetString(5)))
                : null;
        }

        if (removed is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new DraftAttachmentMutationResult(
                await ReadThreadDraftAsync(connection, threadId, cancellationToken).ConfigureAwait(false),
                DraftAttachmentMutationState.AttachmentNotFound);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM DraftAttachments WHERE AttachmentId = $attachmentId;";
            delete.Parameters.AddWithValue("$attachmentId", attachmentId.Value);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await IncrementDraftRevisionAsync(
            connection,
            transaction,
            threadId,
            draftId,
            expectedRevision,
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new DraftAttachmentMutationResult(
            await ReadThreadDraftAsync(connection, threadId, cancellationToken).ConfigureAwait(false),
            DraftAttachmentMutationState.Updated,
            removed);
    }

    public async Task<DraftClearResult> ClearThreadDraftAsync(
        ThreadId threadId,
        DraftId draftId,
        long expectedRevision,
        IReadOnlyList<AttachmentId> expectedAttachmentIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedAttachmentIds);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var current = await ReadDraftHeaderAsync(connection, transaction, threadId, cancellationToken)
            .ConfigureAwait(false);
        if (current is null || current.Value.DraftId != draftId)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new DraftClearResult(null, DraftAttachmentMutationState.DraftNotFound, []);
        }

        if (current.Value.Revision != expectedRevision)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new DraftClearResult(
                await ReadThreadDraftAsync(connection, threadId, cancellationToken).ConfigureAwait(false),
                DraftAttachmentMutationState.DraftConflict,
                []);
        }

        var attachments = await ReadDraftAttachmentsAsync(
            connection,
            transaction,
            threadId,
            draftId,
            cancellationToken).ConfigureAwait(false);
        if (!attachments.Select(static attachment => attachment.AttachmentId)
                .SequenceEqual(expectedAttachmentIds))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new DraftClearResult(
                await ReadThreadDraftAsync(connection, threadId, cancellationToken).ConfigureAwait(false),
                DraftAttachmentMutationState.DraftConflict,
                []);
        }

        await using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM DraftAttachments WHERE DraftId = $draftId;";
            delete.Parameters.AddWithValue("$draftId", draftId.Value);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE ThreadDrafts
                SET DraftText = '', Revision = Revision + 1, UpdatedUtc = $updatedUtc
                WHERE ThreadId = $threadId AND DraftId = $draftId AND Revision = $expectedRevision;
                """;
            update.Parameters.AddWithValue("$updatedUtc", FormatDate(DateTimeOffset.UtcNow));
            update.Parameters.AddWithValue("$threadId", threadId.Value);
            update.Parameters.AddWithValue("$draftId", draftId.Value);
            update.Parameters.AddWithValue("$expectedRevision", expectedRevision);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("The draft revision changed while it was being cleared.");
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new DraftClearResult(
            await ReadThreadDraftAsync(connection, threadId, cancellationToken).ConfigureAwait(false),
            DraftAttachmentMutationState.Updated,
            attachments);
    }

    public async Task<ReceiptAcquisition> AcquireReceiptAsync(
        CommandReceipt receipt,
        string bodyHash,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT OR IGNORE INTO CommandReceipts
                (ClientId, CommandId, EnvironmentId, ThreadId, BodyHash, State, ErrorCode, CreatedUtc, UpdatedUtc)
            VALUES
                ($clientId, $commandId, $environmentId, $threadId, $bodyHash, $state, NULL, $createdUtc, $updatedUtc);
            """;
        insert.Parameters.AddWithValue("$clientId", receipt.ClientId.Value);
        insert.Parameters.AddWithValue("$commandId", receipt.CommandId.Value);
        insert.Parameters.AddWithValue("$environmentId", receipt.EnvironmentId.Value);
        insert.Parameters.AddWithValue("$threadId", receipt.ThreadId.Value);
        insert.Parameters.AddWithValue("$bodyHash", bodyHash);
        insert.Parameters.AddWithValue("$state", receipt.State.ToString());
        insert.Parameters.AddWithValue("$createdUtc", FormatDate(receipt.CreatedUtc));
        insert.Parameters.AddWithValue("$updatedUtc", FormatDate(receipt.UpdatedUtc));
        var inserted = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        var stored = await GetStoredReceiptAsync(connection, receipt.ClientId, receipt.CommandId, cancellationToken)
            .ConfigureAwait(false);
        return new ReceiptAcquisition(
            stored ?? throw new InvalidOperationException("The command receipt was not persisted."),
            inserted);
    }

    public async Task<StoredCommandReceipt?> GetReceiptAsync(
        ClientId clientId,
        CommandId commandId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetStoredReceiptAsync(connection, clientId, commandId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<CommandReceipt> UpdateReceiptStateAsync(
        ClientId clientId,
        CommandId commandId,
        CommandReceiptState state,
        string? errorCode = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var current = await GetStoredReceiptAsync(connection, clientId, commandId, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("Command receipt was not found.");
        if (IsTerminal(current.Receipt.State))
        {
            return current.Receipt;
        }

        var updatedUtc = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE CommandReceipts
            SET State = $state, ErrorCode = $errorCode, UpdatedUtc = $updatedUtc
            WHERE ClientId = $clientId AND CommandId = $commandId
              AND State NOT IN ($completed, $rejected, $failed, $uncertain);
            """;
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$errorCode", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$updatedUtc", FormatDate(updatedUtc));
        command.Parameters.AddWithValue("$clientId", clientId.Value);
        command.Parameters.AddWithValue("$commandId", commandId.Value);
        command.Parameters.AddWithValue("$completed", CommandReceiptState.Completed.ToString());
        command.Parameters.AddWithValue("$rejected", CommandReceiptState.Rejected.ToString());
        command.Parameters.AddWithValue("$failed", CommandReceiptState.Failed.ToString());
        command.Parameters.AddWithValue("$uncertain", CommandReceiptState.DispatchUncertain.ToString());
        var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        if (changed)
        {
            return current.Receipt with { State = state, ErrorCode = errorCode, UpdatedUtc = updatedUtc };
        }

        return (await GetStoredReceiptAsync(connection, clientId, commandId, cancellationToken)
            .ConfigureAwait(false))!.Receipt;
    }

    public async Task<WorkspaceReceiptAcquisition> AcquireWorkspaceReceiptAsync(
        WorkspaceCommandReceipt receipt,
        string bodyHash,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT OR IGNORE INTO WorkspaceCommandReceipts
                (ClientId, CommandId, EnvironmentId, ProjectId, ThreadId, BodyHash, State,
                 ErrorCode, ResultJson, CreatedUtc, UpdatedUtc)
            VALUES
                ($clientId, $commandId, $environmentId, $projectId, $threadId, $bodyHash, $state,
                 NULL, NULL, $createdUtc, $updatedUtc);
            """;
        insert.Parameters.AddWithValue("$clientId", receipt.ClientId.Value);
        insert.Parameters.AddWithValue("$commandId", receipt.CommandId.Value);
        insert.Parameters.AddWithValue("$environmentId", receipt.EnvironmentId.Value);
        insert.Parameters.AddWithValue("$projectId", receipt.ProjectId.Value);
        insert.Parameters.AddWithValue("$threadId", receipt.ThreadId is null ? DBNull.Value : receipt.ThreadId.Value.Value);
        insert.Parameters.AddWithValue("$bodyHash", bodyHash);
        insert.Parameters.AddWithValue("$state", receipt.State.ToString());
        insert.Parameters.AddWithValue("$createdUtc", FormatDate(receipt.CreatedUtc));
        insert.Parameters.AddWithValue("$updatedUtc", FormatDate(receipt.UpdatedUtc));
        var inserted = await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        var stored = await GetStoredWorkspaceReceiptAsync(
            connection,
            receipt.ClientId,
            receipt.CommandId,
            cancellationToken).ConfigureAwait(false);
        return new WorkspaceReceiptAcquisition(
            stored ?? throw new InvalidOperationException("The workspace command receipt was not persisted."),
            inserted);
    }

    public async Task<StoredWorkspaceCommandReceipt?> GetWorkspaceReceiptAsync(
        ClientId clientId,
        CommandId commandId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await GetStoredWorkspaceReceiptAsync(connection, clientId, commandId, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<StoredWorkspaceCommandReceipt> UpdateWorkspaceReceiptAsync(
        ClientId clientId,
        CommandId commandId,
        CommandReceiptState state,
        string? errorCode = null,
        WorkspaceGitOperationResult? result = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var current = await GetStoredWorkspaceReceiptAsync(connection, clientId, commandId, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("Workspace command receipt was not found.");
        if (IsTerminal(current.Receipt.State))
        {
            return current;
        }

        var updatedUtc = DateTimeOffset.UtcNow;
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE WorkspaceCommandReceipts
            SET State = $state, ErrorCode = $errorCode, ResultJson = $resultJson, UpdatedUtc = $updatedUtc
            WHERE ClientId = $clientId AND CommandId = $commandId
              AND State NOT IN ($completed, $rejected, $failed, $uncertain);
            """;
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$errorCode", (object?)errorCode ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$resultJson",
            result is null
                ? DBNull.Value
                : JsonSerializer.Serialize(result, ProtocolJsonContext.Default.WorkspaceGitOperationResult));
        command.Parameters.AddWithValue("$updatedUtc", FormatDate(updatedUtc));
        command.Parameters.AddWithValue("$clientId", clientId.Value);
        command.Parameters.AddWithValue("$commandId", commandId.Value);
        command.Parameters.AddWithValue("$completed", CommandReceiptState.Completed.ToString());
        command.Parameters.AddWithValue("$rejected", CommandReceiptState.Rejected.ToString());
        command.Parameters.AddWithValue("$failed", CommandReceiptState.Failed.ToString());
        command.Parameters.AddWithValue("$uncertain", CommandReceiptState.DispatchUncertain.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return await GetStoredWorkspaceReceiptAsync(connection, clientId, commandId, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("Workspace command receipt disappeared.");
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<HostEnvironmentRecord?> ReadEnvironmentAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EnvironmentId, Name, CreatedUtc FROM Environment LIMIT 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new HostEnvironmentRecord(
                EnvironmentId.Parse(reader.GetString(0)),
                reader.GetString(1),
                ParseDate(reader.GetString(2)))
            : null;
    }

    private static async Task<StoredCommandReceipt?> GetStoredReceiptAsync(
        SqliteConnection connection,
        ClientId clientId,
        CommandId commandId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EnvironmentId, ClientId, CommandId, ThreadId, BodyHash, State, ErrorCode,
                   CreatedUtc, UpdatedUtc
            FROM CommandReceipts WHERE ClientId = $clientId AND CommandId = $commandId;
            """;
        command.Parameters.AddWithValue("$clientId", clientId.Value);
        command.Parameters.AddWithValue("$commandId", commandId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var receipt = new CommandReceipt(
            EnvironmentId.Parse(reader.GetString(0)),
            ClientId.Parse(reader.GetString(1)),
            CommandId.Parse(reader.GetString(2)),
            ThreadId.Parse(reader.GetString(3)),
            Enum.Parse<CommandReceiptState>(reader.GetString(5), ignoreCase: false),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            ParseDate(reader.GetString(7)),
            ParseDate(reader.GetString(8)));
        return new StoredCommandReceipt(receipt, reader.GetString(4));
    }

    private static async Task<StoredWorkspaceCommandReceipt?> GetStoredWorkspaceReceiptAsync(
        SqliteConnection connection,
        ClientId clientId,
        CommandId commandId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EnvironmentId, ClientId, CommandId, ProjectId, ThreadId, BodyHash, State,
                   ErrorCode, ResultJson, CreatedUtc, UpdatedUtc
            FROM WorkspaceCommandReceipts
            WHERE ClientId = $clientId AND CommandId = $commandId;
            """;
        command.Parameters.AddWithValue("$clientId", clientId.Value);
        command.Parameters.AddWithValue("$commandId", commandId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var receipt = new WorkspaceCommandReceipt(
            EnvironmentId.Parse(reader.GetString(0)),
            ClientId.Parse(reader.GetString(1)),
            CommandId.Parse(reader.GetString(2)),
            ProjectId.Parse(reader.GetString(3)),
            reader.IsDBNull(4) ? null : ThreadId.Parse(reader.GetString(4)),
            Enum.Parse<CommandReceiptState>(reader.GetString(6), ignoreCase: false),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            ParseDate(reader.GetString(9)),
            ParseDate(reader.GetString(10)));
        var result = reader.IsDBNull(8)
            ? null
            : JsonSerializer.Deserialize(
                reader.GetString(8),
                ProtocolJsonContext.Default.WorkspaceGitOperationResult);
        return new StoredWorkspaceCommandReceipt(receipt, reader.GetString(5), result);
    }

    private async Task<ThreadDraft?> ReadThreadDraftAsync(
        SqliteConnection connection,
        ThreadId threadId,
        CancellationToken cancellationToken)
    {
        var header = await ReadDraftHeaderAsync(connection, null, threadId, cancellationToken).ConfigureAwait(false);
        if (header is null)
        {
            return null;
        }

        var attachments = await ReadDraftAttachmentsAsync(
            connection,
            null,
            threadId,
            header.Value.DraftId,
            cancellationToken).ConfigureAwait(false);

        return new ThreadDraft(
            EnvironmentId,
            threadId,
            header.Value.DraftId,
            header.Value.Text,
            header.Value.Revision,
            header.Value.UpdatedUtc,
            attachments);
    }

    private async Task<IReadOnlyList<DraftAttachment>> ReadDraftAttachmentsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ThreadId threadId,
        DraftId draftId,
        CancellationToken cancellationToken)
    {
        var attachments = new List<DraftAttachment>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT AttachmentId, FileName, MediaType, ByteLength, Sha256, ServerPath, CreatedUtc
            FROM DraftAttachments
            WHERE DraftId = $draftId
            ORDER BY Ordinal, AttachmentId;
            """;
        command.Parameters.AddWithValue("$draftId", draftId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            attachments.Add(new DraftAttachment(
                EnvironmentId,
                threadId,
                draftId,
                AttachmentId.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetString(5),
                ParseDate(reader.GetString(6))));
        }

        return attachments;
    }

    private static async Task<(DraftId DraftId, string Text, long Revision, DateTimeOffset UpdatedUtc)?>
        ReadDraftHeaderAsync(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            ThreadId threadId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DraftId, DraftText, Revision, UpdatedUtc
            FROM ThreadDrafts
            WHERE ThreadId = $threadId;
            """;
        command.Parameters.AddWithValue("$threadId", threadId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (DraftId.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt64(2), ParseDate(reader.GetString(3)))
            : null;
    }

    private async Task<ThreadPiConfiguration?> ReadThreadPiConfigurationAsync(
        SqliteConnection connection,
        ThreadId threadId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ModelProvider, ModelId, ThinkingLevel, RuntimeModeId, Revision, UpdatedUtc
            FROM ThreadPiConfigurations
            WHERE ThreadId = $threadId;
            """;
        command.Parameters.AddWithValue("$threadId", threadId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var providerId = reader.IsDBNull(0) ? null : reader.GetString(0);
        var modelId = reader.IsDBNull(1) ? null : reader.GetString(1);
        var model = providerId is null && modelId is null
            ? null
            : providerId is not null && modelId is not null
                ? new PiModelSelection(providerId, modelId)
                : throw new InvalidDataException("The stored Pi model selection is incomplete.");
        PiThinkingLevel? thinkingLevel = reader.IsDBNull(2)
            ? null
            : Enum.Parse<PiThinkingLevel>(reader.GetString(2), ignoreCase: false);
        return new ThreadPiConfiguration(
            EnvironmentId,
            threadId,
            model,
            thinkingLevel,
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt64(4),
            ParseDate(reader.GetString(5)));
    }

    private static async Task IncrementDraftRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ThreadId threadId,
        DraftId draftId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE ThreadDrafts
            SET Revision = Revision + 1, UpdatedUtc = $updatedUtc
            WHERE ThreadId = $threadId AND DraftId = $draftId AND Revision = $expectedRevision;
            """;
        update.Parameters.AddWithValue("$updatedUtc", FormatDate(DateTimeOffset.UtcNow));
        update.Parameters.AddWithValue("$threadId", threadId.Value);
        update.Parameters.AddWithValue("$draftId", draftId.Value);
        update.Parameters.AddWithValue("$expectedRevision", expectedRevision);
        if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new InvalidOperationException("The draft revision changed during the attachment mutation.");
        }
    }

    private ProjectDescriptor ReadProject(SqliteDataReader reader) => new(
        EnvironmentId,
        ProjectId.Parse(reader.GetString(0)),
        reader.GetString(1),
        reader.GetString(2),
        ParseDate(reader.GetString(3)),
        Enum.TryParse<ThreadWorkspaceMode>(reader.GetString(4), out var workspaceMode)
            ? workspaceMode
            : ThreadWorkspaceMode.Local,
        JsonSerializer.Deserialize(reader.GetString(5), ProtocolJsonContext.Default.ProjectScriptArray) ?? [],
        reader.GetInt64(6) != 0);

    private static async Task EnsureProjectConfigurationColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "PRAGMA table_info(Projects);";
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                columns.Add(reader.GetString(1));
            }
        }

        foreach (var (name, declaration) in new[]
                 {
                     ("DefaultWorkspaceMode", "TEXT NOT NULL DEFAULT 'Local'"),
                     ("ScriptsJson", "TEXT NOT NULL DEFAULT '[]'"),
                     ("AreRepositoryScriptsTrusted", "INTEGER NOT NULL DEFAULT 0"),
                 })
        {
            if (columns.Contains(name))
            {
                continue;
            }

            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE Projects ADD COLUMN {name} {declaration};";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static HostThreadRecord ReadThread(SqliteDataReader reader) => new(
        ThreadId.Parse(reader.GetString(0)),
        ProjectId.Parse(reader.GetString(1)),
        reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.GetString(4),
        reader.GetInt64(5),
        reader.GetInt64(6) != 0,
        reader.GetInt64(7) != 0,
        ParseDate(reader.GetString(8)),
        ParseDate(reader.GetString(9)),
        Enum.Parse<ThreadWorkspaceMode>(reader.GetString(10), ignoreCase: false),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        reader.IsDBNull(12) ? null : reader.GetString(12),
        reader.GetInt64(13),
        Enum.Parse<SetupScriptState>(reader.GetString(14), ignoreCase: false),
        reader.IsDBNull(15) ? null : reader.GetString(15));

    private static async Task EnsureThreadLifecycleColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "PRAGMA table_info(Threads);";
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                columns.Add(reader.GetString(1));
            }
        }

        foreach (var (name, declaration) in new[]
                 {
                     ("Revision", "INTEGER NOT NULL DEFAULT 0"),
                     ("IsArchived", "INTEGER NOT NULL DEFAULT 0 CHECK (IsArchived IN (0, 1))"),
                     ("IsPinned", "INTEGER NOT NULL DEFAULT 0 CHECK (IsPinned IN (0, 1))"),
                 })
        {
            if (columns.Contains(name))
            {
                continue;
            }

            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE Threads ADD COLUMN {name} {declaration};";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnsureThreadWorkspaceColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "PRAGMA table_info(Threads);";
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                columns.Add(reader.GetString(1));
            }
        }

        foreach (var (name, declaration) in new[]
                 {
                     ("WorkspaceMode", "TEXT NOT NULL DEFAULT 'Local'"),
                     ("BranchName", "TEXT NULL"),
                     ("WorktreePath", "TEXT NULL"),
                     ("WorkspaceGeneration", "INTEGER NOT NULL DEFAULT 0"),
                     ("SetupScriptState", "TEXT NOT NULL DEFAULT 'None'"),
                     ("SetupScriptMessage", "TEXT NULL"),
                 })
        {
            if (columns.Contains(name))
            {
                continue;
            }

            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE Threads ADD COLUMN {name} {declaration};";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task EnsureThreadCheckpointColumnsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = "PRAGMA table_info(ThreadCheckpoints);";
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                columns.Add(reader.GetString(1));
            }
        }

        foreach (var (name, declaration) in new[]
                 {
                     ("PiEntryIdBefore", "TEXT NULL"),
                     ("BeforeCheckpointRef", "TEXT NULL"),
                     ("WorkspaceGeneration", "INTEGER NOT NULL DEFAULT 0"),
                     ("BranchName", "TEXT NULL"),
                     ("HeadShaBefore", "TEXT NULL"),
                     ("HeadShaAfter", "TEXT NULL"),
                 })
        {
            if (columns.Contains(name))
            {
                continue;
            }

            await using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE ThreadCheckpoints ADD COLUMN {name} {declaration};";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string EscapeLikePattern(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static bool IsTerminal(CommandReceiptState state) => state is
        CommandReceiptState.Completed or
        CommandReceiptState.Rejected or
        CommandReceiptState.Failed or
        CommandReceiptState.DispatchUncertain;

    private static string FormatDate(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
