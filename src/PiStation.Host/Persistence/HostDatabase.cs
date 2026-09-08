using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;
using PiStation.Protocol.Serialization;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Persistence;

public sealed partial class HostDatabase
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
            Cache = SqliteCacheMode.Private,
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

                CREATE TABLE IF NOT EXISTS PiSessionCopies (
                    OperationId TEXT PRIMARY KEY NOT NULL,
                    RequestHash TEXT NOT NULL,
                    ThreadId TEXT NOT NULL
                );

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
                    Icon TEXT NULL,
                    DefaultModelProvider TEXT NULL,
                    DefaultModelId TEXT NULL,
                    DefaultThinkingLevel TEXT NULL,
                    DefaultRuntimeModeId TEXT NULL,
                    AutoPullDefaultBranch INTEGER NOT NULL DEFAULT 0 CHECK (AutoPullDefaultBranch IN (0, 1)),
                    CreatedUtc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS ProjectDefaultsOverrides (
                    ProjectId TEXT PRIMARY KEY NOT NULL,
                    ConfigurationJson TEXT NOT NULL,
                    FOREIGN KEY (ProjectId) REFERENCES Projects(ProjectId) ON DELETE CASCADE
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

                CREATE TABLE IF NOT EXISTS ThreadInboxMetadata (
                    ThreadId TEXT PRIMARY KEY NOT NULL,
                    IsSettled INTEGER NOT NULL DEFAULT 0 CHECK (IsSettled IN (0, 1)),
                    SnoozedUntilUtc TEXT NULL,
                    PinnedOrder INTEGER NULL,
                    TitleKind TEXT NOT NULL DEFAULT 'Placeholder',
                    PullRequestJson TEXT NULL,
                    FOREIGN KEY (ThreadId) REFERENCES Threads(ThreadId) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS PromptStashes (
                    StashId TEXT PRIMARY KEY NOT NULL,
                    ProjectId TEXT NOT NULL,
                    ThreadId TEXT NULL,
                    Title TEXT NOT NULL,
                    StashText TEXT NOT NULL,
                    CreatedUtc TEXT NOT NULL,
                    UpdatedUtc TEXT NOT NULL,
                    FOREIGN KEY (ProjectId) REFERENCES Projects(ProjectId) ON DELETE CASCADE,
                    FOREIGN KEY (ThreadId) REFERENCES Threads(ThreadId) ON DELETE SET NULL
                );

                CREATE INDEX IF NOT EXISTS IX_PromptStashes_ProjectUpdated
                ON PromptStashes(ProjectId, UpdatedUtc DESC);

                CREATE TABLE IF NOT EXISTS UsageEvents (
                    UsageEventId INTEGER PRIMARY KEY AUTOINCREMENT,
                    ThreadId TEXT NOT NULL,
                    Provider TEXT NOT NULL,
                    Model TEXT NOT NULL,
                    InputTokens INTEGER NOT NULL,
                    OutputTokens INTEGER NOT NULL,
                    CacheTokens INTEGER NOT NULL,
                    TotalTokens INTEGER NOT NULL,
                    EstimatedCost TEXT NOT NULL,
                    CreatedUtc TEXT NOT NULL,
                    FOREIGN KEY (ThreadId) REFERENCES Threads(ThreadId) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS PiAutomationSettings (
                    Id INTEGER PRIMARY KEY CHECK(Id=1), AutoCompaction INTEGER NULL, AutoRetry INTEGER NULL, Revision INTEGER NOT NULL);
                INSERT OR IGNORE INTO PiAutomationSettings VALUES(1,NULL,NULL,0);

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

                CREATE TABLE IF NOT EXISTS ThreadAgentEvents (
                    EventId INTEGER PRIMARY KEY AUTOINCREMENT,
                    ThreadId TEXT NOT NULL,
                    TurnId TEXT NULL,
                    TurnCount INTEGER NOT NULL CHECK (TurnCount >= 0),
                    EventJson TEXT NOT NULL,
                    CreatedUtc TEXT NOT NULL,
                    FOREIGN KEY (ThreadId) REFERENCES Threads(ThreadId) ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS IX_ThreadAgentEvents_ThreadEvent
                ON ThreadAgentEvents(ThreadId, EventId);

                CREATE TABLE IF NOT EXISTS SentMessageContents (
                    Id TEXT PRIMARY KEY NOT NULL,
                    ThreadId TEXT NOT NULL,
                    ContentJson TEXT NOT NULL,
                    FOREIGN KEY (ThreadId) REFERENCES Threads(ThreadId) ON DELETE CASCADE
                );
                CREATE INDEX IF NOT EXISTS IX_SentMessageContents_Thread ON SentMessageContents(ThreadId);

                CREATE TABLE IF NOT EXISTS BackgroundTaskSubmissions (
                    SourceThreadId TEXT NOT NULL,
                    DraftId TEXT NOT NULL,
                    DraftRevision INTEGER NOT NULL,
                    RequestJson TEXT NOT NULL,
                    ResultJson TEXT NOT NULL,
                    PRIMARY KEY(DraftId, DraftRevision),
                    FOREIGN KEY(SourceThreadId) REFERENCES Threads(ThreadId) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS PullRequestCheckouts (
                    ProjectId TEXT NOT NULL,
                    Repository TEXT NOT NULL,
                    Number TEXT NOT NULL,
                    HeadCommitId TEXT NOT NULL,
                    ThreadId TEXT NOT NULL UNIQUE,
                    Completed INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (ProjectId, Repository, Number, HeadCommitId),
                    FOREIGN KEY (ProjectId) REFERENCES Projects(ProjectId) ON DELETE CASCADE
                );
                CREATE TABLE IF NOT EXISTS HostingOperations (OperationId TEXT PRIMARY KEY NOT NULL, RequestHash TEXT NOT NULL, OperationJson TEXT NOT NULL);

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

        await InitializeSettlementAsync(connection, cancellationToken).ConfigureAwait(false);
        await RecoverHostingOperationsAsync(cancellationToken).ConfigureAwait(false);
        await EnsureProjectConfigurationColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        await using (var unsupportedModes = connection.CreateCommand())
        {
            unsupportedModes.CommandText = "UPDATE ThreadPiConfigurations SET RuntimeModeId = NULL WHERE RuntimeModeId NOT IN ('supervised','auto-accept-edits','auto','full-access'); UPDATE Projects SET DefaultRuntimeModeId = NULL WHERE DefaultRuntimeModeId NOT IN ('supervised','auto-accept-edits','auto','full-access');";
            await unsupportedModes.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await EnsureThreadCheckpointColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsureThreadLifecycleColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsureThreadWorkspaceColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "ThreadDrafts", "ContextJson", "TEXT NOT NULL DEFAULT '[]'", cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "UsageEvents", "CostKnown", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "PromptStashes", "ContextJson", "TEXT NOT NULL DEFAULT '[]'", cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "PromptStashes", "AttachmentsJson", "TEXT NOT NULL DEFAULT '[]'", cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "ThreadInboxMetadata", "CompletionSequence", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await EnsureColumnAsync(connection, "ThreadInboxMetadata", "ReadCompletionSequence", "INTEGER NOT NULL DEFAULT 0", cancellationToken).ConfigureAwait(false);
        await using (var seedInbox = connection.CreateCommand())
        {
            seedInbox.CommandText = """
                INSERT OR IGNORE INTO ThreadInboxMetadata
                    (ThreadId, IsSettled, SnoozedUntilUtc, PinnedOrder, TitleKind, PullRequestJson)
                SELECT ThreadId, 0, NULL, NULL,
                       CASE WHEN Title GLOB 'Thread [0-9]*' THEN 'Placeholder' ELSE 'Manual' END,
                       NULL
                FROM Threads;
                """;
            await seedInbox.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await InitializeCatalogAsync(connection, cancellationToken).ConfigureAwait(false);
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
        string? icon = null,
        PiModelSelection? defaultModel = null,
        PiThinkingLevel? defaultThinkingLevel = null,
        string? defaultRuntimeModeId = null,
        bool autoPullDefaultBranch = false,
        CancellationToken cancellationToken = default)
    {
        var created = new ProjectDescriptor(
            EnvironmentId,
            ProjectId.New(),
            canonicalPath,
            displayName,
            DateTimeOffset.UtcNow,
            defaultWorkspaceMode,
            scripts ?? [],
            false,
            icon,
            defaultModel,
            defaultThinkingLevel,
            defaultRuntimeModeId,
            autoPullDefaultBranch);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT OR IGNORE INTO Projects
                    (ProjectId, CanonicalPath, DisplayName, DefaultWorkspaceMode, ScriptsJson,
                     Icon, DefaultModelProvider, DefaultModelId, DefaultThinkingLevel,
                     DefaultRuntimeModeId, AutoPullDefaultBranch, CreatedUtc)
                VALUES ($projectId, $path, $name, $workspaceMode, $scriptsJson,
                        $icon, $modelProvider, $modelId, $thinkingLevel,
                        $runtimeModeId, $autoPull, $createdUtc);
                """;
            insert.Parameters.AddWithValue("$projectId", created.ProjectId.Value);
            insert.Parameters.AddWithValue("$path", canonicalPath);
            insert.Parameters.AddWithValue("$name", displayName);
            insert.Parameters.AddWithValue("$workspaceMode", defaultWorkspaceMode.ToString());
            insert.Parameters.AddWithValue(
                "$scriptsJson",
                JsonSerializer.Serialize((scripts ?? []).ToArray(), ProtocolJsonContext.Default.ProjectScriptArray));
            insert.Parameters.AddWithValue("$icon", (object?)icon ?? DBNull.Value);
            insert.Parameters.AddWithValue("$modelProvider", (object?)defaultModel?.ProviderId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$modelId", (object?)defaultModel?.ModelId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$thinkingLevel", (object?)defaultThinkingLevel?.ToString() ?? DBNull.Value);
            insert.Parameters.AddWithValue("$runtimeModeId", (object?)defaultRuntimeModeId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$autoPull", autoPullDefaultBranch ? 1 : 0);
            insert.Parameters.AddWithValue("$createdUtc", FormatDate(created.CreatedUtc));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT ProjectId, CanonicalPath, DisplayName, CreatedUtc,
                   DefaultWorkspaceMode, ScriptsJson, AreRepositoryScriptsTrusted,
                   Icon, DefaultModelProvider, DefaultModelId, DefaultThinkingLevel,
                   DefaultRuntimeModeId, AutoPullDefaultBranch
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
                   DefaultWorkspaceMode, ScriptsJson, AreRepositoryScriptsTrusted,
                   Icon, DefaultModelProvider, DefaultModelId, DefaultThinkingLevel,
                   DefaultRuntimeModeId, AutoPullDefaultBranch
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
                   DefaultWorkspaceMode, ScriptsJson, AreRepositoryScriptsTrusted,
                   Icon, DefaultModelProvider, DefaultModelId, DefaultThinkingLevel,
                   DefaultRuntimeModeId, AutoPullDefaultBranch
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
        string? icon = null,
        PiModelSelection? defaultModel = null,
        PiThinkingLevel? defaultThinkingLevel = null,
        string? defaultRuntimeModeId = null,
        bool autoPullDefaultBranch = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Projects
            SET DefaultWorkspaceMode = $workspaceMode,
                ScriptsJson = $scriptsJson,
                Icon = $icon,
                DefaultModelProvider = $modelProvider,
                DefaultModelId = $modelId,
                DefaultThinkingLevel = $thinkingLevel,
                DefaultRuntimeModeId = $runtimeModeId,
                AutoPullDefaultBranch = $autoPull
            WHERE ProjectId = $projectId;
            """;
        command.Parameters.AddWithValue("$workspaceMode", defaultWorkspaceMode.ToString());
        command.Parameters.AddWithValue(
            "$scriptsJson",
            JsonSerializer.Serialize(scripts.ToArray(), ProtocolJsonContext.Default.ProjectScriptArray));
        command.Parameters.AddWithValue("$icon", (object?)icon ?? DBNull.Value);
        command.Parameters.AddWithValue("$modelProvider", (object?)defaultModel?.ProviderId ?? DBNull.Value);
        command.Parameters.AddWithValue("$modelId", (object?)defaultModel?.ModelId ?? DBNull.Value);
        command.Parameters.AddWithValue("$thinkingLevel", (object?)defaultThinkingLevel?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$runtimeModeId", (object?)defaultRuntimeModeId ?? DBNull.Value);
        command.Parameters.AddWithValue("$autoPull", autoPullDefaultBranch ? 1 : 0);
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
                   DefaultWorkspaceMode, ScriptsJson, AreRepositoryScriptsTrusted,
                   Icon, DefaultModelProvider, DefaultModelId, DefaultThinkingLevel,
                   DefaultRuntimeModeId, AutoPullDefaultBranch
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
        await using var inbox = connection.CreateCommand();
        inbox.CommandText = """
            INSERT INTO ThreadInboxMetadata
                (ThreadId, IsSettled, SnoozedUntilUtc, PinnedOrder, TitleKind, PullRequestJson)
            VALUES ($threadId, 0, NULL, NULL, $titleKind, NULL);
            """;
        inbox.Parameters.AddWithValue("$threadId", record.ThreadId.Value);
        inbox.Parameters.AddWithValue(
            "$titleKind",
            title.StartsWith("Thread ", StringComparison.Ordinal) ? ThreadTitleKind.Placeholder.ToString() : ThreadTitleKind.Manual.ToString());
        await inbox.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    public Task<IReadOnlyList<HostThreadRecord>> SearchThreadsAsync(ProjectId projectId, string query, bool includeArchived, int limit, CancellationToken cancellationToken = default) =>
        SearchThreadsPageAsync(projectId, query, includeArchived, limit, 0, cancellationToken);

    public async Task<IReadOnlyList<HostThreadRecord>> SearchThreadsPageAsync(
        ProjectId projectId,
        string query,
        bool includeArchived,
        int limit,
        int offset,
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
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.Value);
        command.Parameters.AddWithValue("$includeArchived", includeArchived ? 1 : 0);
        command.Parameters.AddWithValue("$query", query);
        command.Parameters.AddWithValue("$pattern", $"%{EscapeLikePattern(query)}%");
        command.Parameters.AddWithValue("$offset", offset);
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

            if (wasUpdated && isPinned is not null)
            {
                await using var pinOrder = connection.CreateCommand();
                pinOrder.CommandText = isPinned.Value
                    ? """
                      UPDATE ThreadInboxMetadata
                      SET PinnedOrder = COALESCE(PinnedOrder, (
                          SELECT COALESCE(MAX(metadata.PinnedOrder), -1) + 1
                          FROM ThreadInboxMetadata metadata
                          INNER JOIN Threads candidate ON candidate.ThreadId = metadata.ThreadId
                          WHERE candidate.ProjectId = (
                              SELECT ProjectId FROM Threads WHERE ThreadId = $threadId
                          )
                            AND candidate.IsPinned = 1
                            AND candidate.ThreadId <> $threadId
                      ))
                      WHERE ThreadId = $threadId;
                      """
                    : "UPDATE ThreadInboxMetadata SET PinnedOrder = NULL WHERE ThreadId = $threadId;";
                pinOrder.Parameters.AddWithValue("$threadId", threadId.Value);
                await pinOrder.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

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

    public async Task DeleteProjectAsync(ProjectId projectId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Projects WHERE ProjectId = $projectId;";
        command.Parameters.AddWithValue("$projectId", projectId.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
        {
            throw new KeyNotFoundException($"Project '{projectId}' was not found.");
        }
    }

    public async Task<ThreadDescriptor> EnrichThreadDescriptorAsync(
        HostThreadRecord thread,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await EnrichThreadDescriptorAsync(connection, null, thread, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ThreadDescriptor> EnrichThreadDescriptorAsync(SqliteConnection connection,
        SqliteTransaction? transaction, HostThreadRecord thread, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT IsSettled, SnoozedUntilUtc, PinnedOrder, TitleKind, PullRequestJson,
                   EXISTS(
                       SELECT 1 FROM ThreadDrafts d
                       WHERE d.ThreadId = $threadId
                         AND (length(trim(d.DraftText)) > 0 OR d.ContextJson <> '[]' OR EXISTS(
                             SELECT 1 FROM DraftAttachments a WHERE a.DraftId = d.DraftId
                         ))
                   ), CompletionSequence, ReadCompletionSequence
            FROM ThreadInboxMetadata
            WHERE ThreadId = $threadId;
            """;
        command.Parameters.AddWithValue("$threadId", thread.ThreadId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return thread.ToDescriptor(EnvironmentId);
        }

        PullRequestLink? pullRequest = null;
        if (!reader.IsDBNull(4))
        {
            pullRequest = JsonSerializer.Deserialize(
                reader.GetString(4),
                ProtocolJsonContext.Default.PullRequestLink);
        }

        var inbox = new HostThreadInboxRecord(
            thread.ThreadId,
            reader.GetInt64(0) != 0,
            reader.IsDBNull(1) ? null : ParseDate(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetInt64(2),
            Enum.TryParse<ThreadTitleKind>(reader.GetString(3), out var titleKind)
                ? titleKind
                : ThreadTitleKind.Placeholder,
            pullRequest);
        return thread.ToDescriptor(EnvironmentId, inbox, reader.GetInt64(5) != 0) with
        {
            CompletionSequence = reader.GetInt64(6),
            ReadCompletionSequence = reader.GetInt64(7),
        };
    }

    public async Task<ThreadMetadataUpdateResult> UpdateThreadInboxAsync(
        ThreadId threadId,
        long expectedRevision,
        bool? isSettled = null,
        DateTimeOffset? snoozedUntilUtc = null,
        bool updateSnooze = false,
        long? pinnedOrder = null,
        bool updatePinnedOrder = false,
        ThreadTitleKind? titleKind = null,
        PullRequestLink? pullRequest = null,
        bool updatePullRequest = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        bool wasUpdated;
        await using (var revision = connection.CreateCommand())
        {
            revision.Transaction = transaction;
            revision.CommandText = """
                UPDATE Threads
                SET Revision = Revision + 1, UpdatedUtc = $updatedUtc
                WHERE ThreadId = $threadId AND Revision = $expectedRevision;
                """;
            revision.Parameters.AddWithValue("$updatedUtc", FormatDate(DateTimeOffset.UtcNow));
            revision.Parameters.AddWithValue("$threadId", threadId.Value);
            revision.Parameters.AddWithValue("$expectedRevision", expectedRevision);
            wasUpdated = await revision.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
        }

        if (wasUpdated)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE ThreadInboxMetadata
                SET IsSettled = COALESCE($isSettled, IsSettled),
                    SettlementProtected = CASE WHEN $isSettled = 0 THEN 1 WHEN $isSettled = 1 THEN 0 ELSE SettlementProtected END,
                    SnoozedUntilUtc = CASE WHEN $updateSnooze = 1 THEN $snoozedUntilUtc ELSE SnoozedUntilUtc END,
                    PinnedOrder = CASE WHEN $isSettled = 1 THEN NULL WHEN $updatePinnedOrder = 1 THEN $pinnedOrder ELSE PinnedOrder END,
                    TitleKind = COALESCE($titleKind, TitleKind),
                    PullRequestJson = CASE WHEN $updatePullRequest = 1 THEN $pullRequestJson ELSE PullRequestJson END
                WHERE ThreadId = $threadId;
                """;
            update.Parameters.AddWithValue("$isSettled", isSettled is null ? DBNull.Value : isSettled.Value ? 1 : 0);
            update.Parameters.AddWithValue("$updateSnooze", updateSnooze ? 1 : 0);
            update.Parameters.AddWithValue("$snoozedUntilUtc", snoozedUntilUtc is null ? DBNull.Value : FormatDate(snoozedUntilUtc.Value));
            update.Parameters.AddWithValue("$updatePinnedOrder", updatePinnedOrder ? 1 : 0);
            update.Parameters.AddWithValue("$pinnedOrder", pinnedOrder is null ? DBNull.Value : pinnedOrder.Value);
            update.Parameters.AddWithValue("$titleKind", titleKind is null ? DBNull.Value : titleKind.Value.ToString());
            update.Parameters.AddWithValue(
                "$pullRequestJson",
                pullRequest is null
                    ? DBNull.Value
                    : JsonSerializer.Serialize(pullRequest, ProtocolJsonContext.Default.PullRequestLink));
            update.Parameters.AddWithValue("$updatePullRequest", updatePullRequest ? 1 : 0);
            update.Parameters.AddWithValue("$threadId", threadId.Value);
            await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (isSettled == true)
            {
                update.CommandText = "UPDATE Threads SET IsPinned=0 WHERE ThreadId=$threadId;";
                await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var thread = await GetThreadAsync(threadId, cancellationToken).ConfigureAwait(false);
        return new ThreadMetadataUpdateResult(thread, wasUpdated);
    }

    public async Task SetThreadSettlementAutomaticallyAsync(
        ThreadId threadId,
        bool isSettled,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ThreadInboxMetadata SET IsSettled = $isSettled WHERE ThreadId = $threadId;
            """;
        command.Parameters.AddWithValue("$isSettled", isSettled ? 1 : 0);
        command.Parameters.AddWithValue("$threadId", threadId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetThreadTitleKindAsync(
        ThreadId threadId,
        ThreadTitleKind titleKind,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE ThreadInboxMetadata SET TitleKind = $titleKind WHERE ThreadId = $threadId;";
        command.Parameters.AddWithValue("$titleKind", titleKind.ToString());
        command.Parameters.AddWithValue("$threadId", threadId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ApplyThreadBulkOperationAsync(
        ApplyThreadBulkOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        var ids = request.ThreadIds.Distinct().Take(200).ToArray();
        if (ids.Length == 0)
        {
            return 0;
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var affected = 0;
        foreach (var id in ids)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$threadId", id.Value);
            command.Parameters.AddWithValue("$projectId", request.ProjectId.Value);
            command.Parameters.AddWithValue("$updatedUtc", FormatDate(DateTimeOffset.UtcNow));
            if (request.Operation == ThreadBulkOperation.Delete)
            {
                command.CommandText = "DELETE FROM Threads WHERE ThreadId = $threadId AND ProjectId = $projectId;";
            }
            else if (request.Operation is ThreadBulkOperation.Archive or ThreadBulkOperation.Restore or
                     ThreadBulkOperation.Pin or ThreadBulkOperation.Unpin)
            {
                command.CommandText = request.Operation switch
                {
                    ThreadBulkOperation.Archive => "UPDATE Threads SET IsArchived = 1, Revision = Revision + 1, UpdatedUtc = $updatedUtc WHERE ThreadId = $threadId AND ProjectId = $projectId;",
                    ThreadBulkOperation.Restore => "UPDATE Threads SET IsArchived = 0, Revision = Revision + 1, UpdatedUtc = $updatedUtc WHERE ThreadId = $threadId AND ProjectId = $projectId;",
                    ThreadBulkOperation.Pin => "UPDATE Threads SET IsPinned = 1, Revision = Revision + 1, UpdatedUtc = $updatedUtc WHERE ThreadId = $threadId AND ProjectId = $projectId;",
                    _ => "UPDATE Threads SET IsPinned = 0, Revision = Revision + 1, UpdatedUtc = $updatedUtc WHERE ThreadId = $threadId AND ProjectId = $projectId;",
                };
            }
            else
            {
                command.CommandText = "UPDATE Threads SET Revision = Revision + 1, UpdatedUtc = $updatedUtc WHERE ThreadId = $threadId AND ProjectId = $projectId;";
            }

            var changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (changed == 0 || request.Operation == ThreadBulkOperation.Delete)
            {
                affected += changed;
                continue;
            }

            await using var inbox = connection.CreateCommand();
            inbox.Transaction = transaction;
            inbox.Parameters.AddWithValue("$threadId", id.Value);
            inbox.Parameters.AddWithValue("$snoozedUntil", request.SnoozedUntilUtc is null
                ? DBNull.Value
                : FormatDate(request.SnoozedUntilUtc.Value));
            inbox.CommandText = request.Operation switch
            {
                ThreadBulkOperation.Settle => "UPDATE ThreadInboxMetadata SET IsSettled = 1, SettlementProtected = 0, PinnedOrder = NULL WHERE ThreadId = $threadId; UPDATE Threads SET IsPinned=0 WHERE ThreadId=$threadId;",
                ThreadBulkOperation.Unsettle => "UPDATE ThreadInboxMetadata SET IsSettled = 0, SettlementProtected = 1 WHERE ThreadId = $threadId;",
                ThreadBulkOperation.Snooze => "UPDATE ThreadInboxMetadata SET SnoozedUntilUtc = $snoozedUntil WHERE ThreadId = $threadId;",
                ThreadBulkOperation.Unsnooze => "UPDATE ThreadInboxMetadata SET SnoozedUntilUtc = NULL WHERE ThreadId = $threadId;",
                ThreadBulkOperation.Pin => """
                    UPDATE ThreadInboxMetadata
                    SET PinnedOrder = COALESCE(PinnedOrder, (
                        SELECT COALESCE(MAX(metadata.PinnedOrder), -1) + 1
                        FROM ThreadInboxMetadata metadata
                        INNER JOIN Threads candidate ON candidate.ThreadId = metadata.ThreadId
                        WHERE candidate.ProjectId = $projectId
                          AND candidate.IsPinned = 1
                          AND candidate.ThreadId <> $threadId
                    ))
                    WHERE ThreadId = $threadId;
                    """,
                ThreadBulkOperation.Unpin => "UPDATE ThreadInboxMetadata SET PinnedOrder = NULL WHERE ThreadId = $threadId;",
                _ => "SELECT 1;",
            };
            inbox.Parameters.AddWithValue("$projectId", request.ProjectId.Value);
            await inbox.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            affected++;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return affected;
    }

    public async Task SetThreadPinnedOrderAsync(
        SetThreadPinnedOrderRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var order = 0L;
        foreach (var id in request.ThreadIdsInOrder.Distinct().Take(200))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE ThreadInboxMetadata SET PinnedOrder = $order
                WHERE ThreadId = $threadId AND EXISTS(
                    SELECT 1 FROM Threads WHERE ThreadId = $threadId AND ProjectId = $projectId AND IsPinned = 1
                );
                """;
            command.Parameters.AddWithValue("$order", order++);
            command.Parameters.AddWithValue("$threadId", id.Value);
            command.Parameters.AddWithValue("$projectId", request.ProjectId.Value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendUsageAsync(
        ThreadId threadId,
        string provider,
        string model,
        long inputTokens,
        long outputTokens,
        long cacheTokens,
        long totalTokens,
        decimal? estimatedCost,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO UsageEvents
                (ThreadId, Provider, Model, InputTokens, OutputTokens, CacheTokens,
                 TotalTokens, EstimatedCost, CostKnown, CreatedUtc)
            VALUES
                ($threadId, $provider, $model, $input, $output, $cache, $total, $cost, $costKnown, $createdUtc);
            """;
        command.Parameters.AddWithValue("$threadId", threadId.Value);
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$input", inputTokens);
        command.Parameters.AddWithValue("$output", outputTokens);
        command.Parameters.AddWithValue("$cache", cacheTokens);
        command.Parameters.AddWithValue("$total", totalTokens);
        command.Parameters.AddWithValue("$cost", (estimatedCost ?? 0).ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$costKnown", estimatedCost is null ? 0 : 1);
        command.Parameters.AddWithValue("$createdUtc", FormatDate(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<UsageSummary> GetUsageSummaryAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<UsageBreakdown>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Provider, Model, SUM(InputTokens), SUM(OutputTokens), SUM(CacheTokens),
                   SUM(TotalTokens), CASE WHEN MIN(CostKnown) = 1 THEN SUM(CAST(EstimatedCost AS REAL)) ELSE NULL END
            FROM UsageEvents
            WHERE CreatedUtc >= $fromUtc AND CreatedUtc <= $toUtc
            GROUP BY Provider, Model
            ORDER BY SUM(TotalTokens) DESC;
            """;
        command.Parameters.AddWithValue("$fromUtc", FormatDate(fromUtc));
        command.Parameters.AddWithValue("$toUtc", FormatDate(toUtc));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new UsageBreakdown(
                reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.IsDBNull(6) ? null : Convert.ToDecimal(reader.GetDouble(6), CultureInfo.InvariantCulture)));
        }

        return new UsageSummary(
            fromUtc,
            toUtc,
            rows.Sum(static row => row.TotalTokens),
            rows.Count == 0 || rows.Any(static row => row.EstimatedCost is null) ? null : rows.Sum(static row => row.EstimatedCost),
            rows,
            "Provider-managed",
            "Pi providers do not expose a portable quota API; open the provider dashboard for authoritative limits.");
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

    public async Task<IReadOnlyList<AgentActivityChangedEvent>> ListThreadAgentEventsAsync(
        ThreadId threadId,
        CancellationToken cancellationToken = default)
    {
        var events = new List<AgentActivityChangedEvent>();
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EventJson
            FROM ThreadAgentEvents
            WHERE ThreadId = $threadId
            ORDER BY EventId;
            """;
        command.Parameters.AddWithValue("$threadId", threadId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var @event = JsonSerializer.Deserialize(
                reader.GetString(0),
                ProtocolJsonContext.Default.AgentActivityChangedEvent);
            if (@event is not null)
            {
                events.Add(@event);
            }
        }

        return events;
    }

    public async Task AppendThreadAgentEventAsync(
        ThreadId threadId,
        TurnId? turnId,
        int turnCount,
        AgentActivityChangedEvent @event,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var insert = connection.CreateCommand())
        {
            insert.Transaction = (SqliteTransaction)transaction;
            insert.CommandText = """
                INSERT INTO ThreadAgentEvents (ThreadId, TurnId, TurnCount, EventJson, CreatedUtc)
                VALUES ($threadId, $turnId, $turnCount, $eventJson, $createdUtc);
                """;
            insert.Parameters.AddWithValue("$threadId", threadId.Value);
            insert.Parameters.AddWithValue("$turnId", (object?)turnId?.Value ?? DBNull.Value);
            insert.Parameters.AddWithValue("$turnCount", Math.Max(0, turnCount));
            insert.Parameters.AddWithValue(
                "$eventJson",
                JsonSerializer.Serialize(@event, ProtocolJsonContext.Default.AgentActivityChangedEvent));
            insert.Parameters.AddWithValue("$createdUtc", FormatDate(DateTimeOffset.UtcNow));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var prune = connection.CreateCommand())
        {
            prune.Transaction = (SqliteTransaction)transaction;
            prune.CommandText = """
                DELETE FROM ThreadAgentEvents
                WHERE ThreadId = $threadId
                  AND EventId NOT IN (
                      SELECT EventId
                      FROM ThreadAgentEvents
                      WHERE ThreadId = $threadId
                      ORDER BY EventId DESC
                      LIMIT 2000
                  );
                """;
            prune.Parameters.AddWithValue("$threadId", threadId.Value);
            await prune.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteThreadAgentEventsAfterTurnAsync(
        ThreadId threadId,
        int turnCount,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM ThreadAgentEvents
            WHERE ThreadId = $threadId AND TurnCount > $turnCount;
            """;
        command.Parameters.AddWithValue("$threadId", threadId.Value);
        command.Parameters.AddWithValue("$turnCount", Math.Max(0, turnCount));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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

    /// <summary>Reads the saved configuration without creating a row for the thread.</summary>
    public async Task<ThreadPiConfiguration?> GetThreadPiConfigurationAsync(
        ThreadId threadId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadThreadPiConfigurationAsync(connection, threadId, cancellationToken).ConfigureAwait(false);
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

    public Task<DraftUpdateResult> UpdateThreadDraftAsync(
        ThreadId threadId, DraftId draftId, long expectedRevision, string text,
        CancellationToken cancellationToken = default) =>
        UpdateThreadDraftAsync(threadId, draftId, expectedRevision, text, null, cancellationToken);
    /// <summary>Reads a draft without initializing one as a side effect.</summary>
    public async Task<ThreadDraft?> GetThreadDraftAsync(
        ThreadId threadId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadThreadDraftAsync(connection, threadId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DraftUpdateResult> UpdateThreadDraftAsync(
        ThreadId threadId,
        DraftId draftId,
        long expectedRevision,
        string text,
        IReadOnlyList<ComposerContext>? context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        ComposerContextDefaults.Validate(context);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var update = connection.CreateCommand();
        update.CommandText = """
            UPDATE ThreadDrafts
            SET DraftText = $text, ContextJson = $context, Revision = Revision + 1, UpdatedUtc = $updatedUtc
            WHERE ThreadId = $threadId AND DraftId = $draftId AND Revision = $expectedRevision;
            """;
        update.Parameters.AddWithValue("$text", text);
        update.Parameters.AddWithValue("$context", JsonSerializer.Serialize((context ?? []).ToArray(), ProtocolJsonContext.Default.ComposerContextArray));
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
                SET DraftText = '', ContextJson = '[]', Revision = Revision + 1, UpdatedUtc = $updatedUtc
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
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
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
            attachments,
            JsonSerializer.Deserialize(header.Value.ContextJson, ProtocolJsonContext.Default.ComposerContextArray) ?? []);
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

    private static async Task<(DraftId DraftId, string Text, long Revision, DateTimeOffset UpdatedUtc, string ContextJson)?>
        ReadDraftHeaderAsync(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            ThreadId threadId,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DraftId, DraftText, Revision, UpdatedUtc, ContextJson
            FROM ThreadDrafts
            WHERE ThreadId = $threadId;
            """;
        command.Parameters.AddWithValue("$threadId", threadId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? (DraftId.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt64(2), ParseDate(reader.GetString(3)), reader.GetString(4))
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

    private ProjectDescriptor ReadProject(SqliteDataReader reader)
    {
        var provider = reader.IsDBNull(8) ? null : reader.GetString(8);
        var modelId = reader.IsDBNull(9) ? null : reader.GetString(9);
        var model = provider is not null && modelId is not null
            ? new PiModelSelection(provider, modelId)
            : null;
        var thinking = !reader.IsDBNull(10) &&
                       Enum.TryParse<PiThinkingLevel>(reader.GetString(10), out var parsedThinking)
            ? parsedThinking
            : (PiThinkingLevel?)null;
        return new ProjectDescriptor(
            EnvironmentId,
            ProjectId.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            ParseDate(reader.GetString(3)),
            Enum.TryParse<ThreadWorkspaceMode>(reader.GetString(4), out var workspaceMode)
                ? workspaceMode
                : ThreadWorkspaceMode.Local,
            JsonSerializer.Deserialize(reader.GetString(5), ProtocolJsonContext.Default.ProjectScriptArray) ?? [],
            reader.GetInt64(6) != 0,
            reader.IsDBNull(7) ? null : reader.GetString(7),
            model,
            thinking,
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.GetInt64(12) != 0);
    }

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
                     ("Icon", "TEXT NULL"),
                     ("DefaultModelProvider", "TEXT NULL"),
                     ("DefaultModelId", "TEXT NULL"),
                     ("DefaultThinkingLevel", "TEXT NULL"),
                     ("DefaultRuntimeModeId", "TEXT NULL"),
                     ("AutoPullDefaultBranch", "INTEGER NOT NULL DEFAULT 0"),
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

    private static async Task EnsureColumnAsync(
        SqliteConnection connection, string table, string name, string declaration, CancellationToken cancellationToken)
    {
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = $"PRAGMA table_info({table});";
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.GetString(1) == name) return;
            }
        }

        await using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {name} {declaration};";
        await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string EscapeLikePattern(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private static string CreatePromptStashTitle(string text)
    {
        var normalized = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0)
        {
            return "Untitled prompt";
        }

        return normalized.Length <= 60 ? normalized : $"{normalized[..57]}…";
    }

    private static bool IsTerminal(CommandReceiptState state) => state is
        CommandReceiptState.Completed or
        CommandReceiptState.Rejected or
        CommandReceiptState.Failed or
        CommandReceiptState.DispatchUncertain;

    private static string FormatDate(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
