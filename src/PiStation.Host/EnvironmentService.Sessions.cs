using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PiStation.Host.Errors;
using PiStation.Host.Projects;
using PiStation.Host.Threads;
using PiStation.Host.Sessions;
using PiStation.PiRpc.Sessions;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Host;

public sealed partial class EnvironmentService
{
    private readonly SemaphoreSlim _sessionCopyGate = new(1, 1);

    public async Task<PiSessionBrowserResult> BrowsePiSessionsAsync(BrowsePiSessionsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(request.Offset);
        var agentDirectory = Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent");
        var directory = Path.GetFullPath(string.IsNullOrWhiteSpace(request.Directory) ? Path.Combine(agentDirectory, "sessions") : request.Directory);
        if (!Directory.Exists(directory)) return new(directory, [], false, 0);
        var candidates = new List<PiSessionCandidate>();
        var skipped = 0;
        var files = Directory.EnumerateFiles(directory, "*.jsonl", new EnumerationOptions
        {
            RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
        }).Order(StringComparer.OrdinalIgnoreCase).Skip(request.Offset).Take(501).ToArray();
        foreach (var file in files.Take(500))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.StartsWith(_options.SessionRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
            try
            {
                var session = await PiSessionDocument.ReadAsync(file, cancellationToken).ConfigureAwait(false);
                candidates.Add(new(file, session.Title, session.ProjectDirectory, session.Revision, session.Entries.Count, File.GetLastWriteTimeUtc(file)));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or DecoderFallbackException) { skipped++; }
        }
        return new(directory, candidates.OrderByDescending(item => item.ModifiedUtc).ToArray(), files.Length > 500, skipped,
            files.Length > 500 ? request.Offset + 500 : null);
    }

    public async Task<PiSessionSnapshot> InspectPiSessionAsync(ThreadId threadId, CancellationToken cancellationToken = default)
    {
        var controller = await _threads.GetAsync(threadId, cancellationToken).ConfigureAwait(false);
        return await controller.WithSessionAsync((document, path) => Task.FromResult(CreateSessionSnapshot(threadId, path, document)), cancellationToken).ConfigureAwait(false);
    }

    public async Task<PiSessionSnapshot> InspectPiSessionPageAsync(PiSessionPageRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Offset < 0 || request.Limit is < 1 or > 5000) throw new ArgumentException("Invalid session page size or offset.");
        var controller = await _threads.GetAsync(request.ThreadId, cancellationToken).ConfigureAwait(false);
        return await controller.WithSessionAsync((document, path) =>
        {
            if (request.ExpectedRevision is not null && request.ExpectedRevision != document.Revision)
                throw new InvalidDataException("The session changed while paging. Inspect it again to load a consistent tree.");
            return Task.FromResult(CreateSessionSnapshot(request.ThreadId, path, document, request.Offset, request.Limit));
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ThreadDescriptor> CopyPiSessionAsync(CopyPiSessionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.OperationId == Guid.Empty || (request.SourceThreadId is null) == string.IsNullOrWhiteSpace(request.SourcePath) ||
            request.SourceThreadId is null && request.EntryId is not null)
            throw new InvalidDataException("Choose one source session and a new operation identity.");
        var hash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, ProtocolJsonContext.Default.CopyPiSessionRequest)));
        await _sessionCopyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (await _database.FindSessionCopyAsync(request.OperationId, hash, cancellationToken).ConfigureAwait(false) is { } previous)
                return await _database.EnrichThreadDescriptorAsync(previous, cancellationToken).ConfigureAwait(false);
            if (await _database.GetThreadAsync(ThreadId.Parse(request.OperationId.ToString("N")), cancellationToken).ConfigureAwait(false) is not null)
                throw new InvalidDataException("This identity already belongs to another thread. Start a new import or fork.");
            var project = await _database.GetProjectAsync(request.ProjectId, cancellationToken).ConfigureAwait(false)
                ?? throw new HostOperationException(ProtocolErrorCodes.ProjectNotFound, "Select a project for the new thread.");
            if (!Directory.Exists(project.CanonicalPath)) throw new DirectoryNotFoundException("The target project folder no longer exists.");
            IReadOnlyList<SentMessageContent> sourceMessages = [];
            async Task<ThreadDescriptor> CopyAsync(PiSessionDocument document, string sourcePath)
            {
                if (request.ExpectedRevision is not null && request.ExpectedRevision != document.Revision)
                    throw new InvalidDataException("The source session changed. Refresh it before importing or forking.");
                if (request.SourceThreadId is not null && string.IsNullOrWhiteSpace(request.ExpectedRevision))
                    throw new InvalidDataException("Refresh the session tree before forking.");
                var title = ThreadMetadataValidation.NormalizeTitle(string.IsNullOrWhiteSpace(request.Title)
                    ? (request.SourceThreadId is null ? "Imported: " : "Fork: ") + document.Title : request.Title);
                var id = request.OperationId.ToString("N");
                var destination = Path.Combine(_options.SessionRoot, id + ".jsonl");
                var bytes = document.Copy(id, project.CanonicalPath, sourcePath, request.EntryId);
                var text = string.Join('\n', Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => JsonNode.Parse(line)!.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))) + "\n";
                var copiedMessages = new List<SentMessageContent>();
                foreach (var message in sourceMessages)
                {
                    if (!text.Contains("<pistation_message_ref>" + message.Id + "</pistation_message_ref>", StringComparison.Ordinal)) continue;
                    var newMessageId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id + ":" + message.Id)))[..32].ToLowerInvariant();
                    text = text.Replace("<pistation_message_ref>" + message.Id + "</pistation_message_ref>",
                        "<pistation_message_ref>" + newMessageId + "</pistation_message_ref>", StringComparison.Ordinal);
                    copiedMessages.Add(message with { Id = newMessageId, Attachments = message.Attachments.Select(attachment => attachment with
                        { EnvironmentId = _database.EnvironmentId, ThreadId = ThreadId.Parse(id) }).ToArray(),
                        Citations = message.Citations.Select(citation => citation.SourceThreadId is { } citedThread &&
                            (citedThread == request.SourceThreadId || string.Equals(citedThread.Value, document.SessionId, StringComparison.OrdinalIgnoreCase))
                                ? citation with { SourceThreadId = ThreadId.Parse(id) } : citation).ToArray() });
                }
                bytes = Encoding.UTF8.GetBytes(text);
                // A crash after the file write but before the database transaction is safely replayable.
                if (File.Exists(destination))
                {
                    if (!(await File.ReadAllBytesAsync(destination, cancellationToken).ConfigureAwait(false)).AsSpan().SequenceEqual(bytes))
                        throw new InvalidDataException("An unfinished session operation has different content. Start a new import or fork.");
                }
                else await WriteSessionFileAsync(destination, bytes, overwrite: false, cancellationToken).ConfigureAwait(false);
                var configuration = document.Configuration(request.EntryId);
                PiModelSelection? model = configuration.Provider is { Length: > 0 } provider && configuration.Model is { Length: > 0 } modelId ? new(provider, modelId) : null;
                PiThinkingLevel? thinking = Enum.TryParse<PiThinkingLevel>(configuration.Thinking, true, out var parsed) ? parsed : null;
                var record = await _database.CreateSessionCopyAsync(request.OperationId, hash, project.ProjectId, title, destination, model, thinking, copiedMessages, cancellationToken).ConfigureAwait(false);
                return await _database.EnrichThreadDescriptorAsync(record, cancellationToken).ConfigureAwait(false);
            }
            if (request.SourceThreadId is { } sourceThread)
            {
                sourceMessages = (await _database.ListSentMessagesAsync(sourceThread, cancellationToken).ConfigureAwait(false)).Values.ToArray();
                var controller = await _threads.GetAsync(sourceThread, cancellationToken).ConfigureAwait(false);
                return await controller.WithSessionAsync(CopyAsync, cancellationToken).ConfigureAwait(false);
            }
            var source = Path.GetFullPath(request.SourcePath!);
            if (source.StartsWith(_options.SessionRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Use Copy whole session or Fork for PiStation-managed sessions.");
            if (string.Equals(Path.GetExtension(source), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                var bundle = await PiSessionBundle.ImportAsync(source, Path.Combine(_options.AttachmentRoot, "imports", request.OperationId.ToString("N")), cancellationToken).ConfigureAwait(false);
                sourceMessages = bundle.Messages;
                return await CopyAsync(bundle.Document, source).ConfigureAwait(false);
            }
            if (!string.Equals(Path.GetExtension(source), ".jsonl", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Select a Pi JSONL session or ZIP bundle.");
            return await CopyAsync(await PiSessionDocument.ReadAsync(source, cancellationToken).ConfigureAwait(false), source).ConfigureAwait(false);
        }
        finally { _sessionCopyGate.Release(); }
    }

    public async Task<PiSessionExportResult> ExportPiSessionAsync(ExportPiSessionRequest request, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(request.Format)) throw new InvalidDataException("Select JSONL or HTML export.");
        var destination = Path.GetFullPath(request.DestinationPath);
        var expectedExtension = request.Format switch { PiSessionExportFormat.Jsonl => ".jsonl", PiSessionExportFormat.Bundle => ".zip", _ => ".html" };
        if (!string.Equals(Path.GetExtension(destination), expectedExtension, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The export filename must end in " + expectedExtension + ".");
        if (destination.StartsWith(_options.CanonicalDataRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose an export location outside PiStation's application data.");
        var controller = await _threads.GetAsync(request.ThreadId, cancellationToken).ConfigureAwait(false);
        return await controller.WithSessionAsync(async (document, source) =>
        {
            if (string.Equals(destination, source, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Choose a different file from the active session.");
            if (request.Format == PiSessionExportFormat.Bundle)
            {
                var messages = await _database.ListSentMessagesAsync(request.ThreadId, cancellationToken).ConfigureAwait(false);
                var length = await PiSessionBundle.ExportAsync(destination, source, document, messages.Values.ToArray(), cancellationToken).ConfigureAwait(false);
                return new PiSessionExportResult(destination, length);
            }
            var bytes = request.Format == PiSessionExportFormat.Jsonl ? await File.ReadAllBytesAsync(source, cancellationToken).ConfigureAwait(false)
                : Encoding.UTF8.GetBytes(document.ToHtml(document.Title));
            await WriteSessionFileAsync(destination, bytes, overwrite: true, cancellationToken).ConfigureAwait(false);
            return new PiSessionExportResult(destination, bytes.Length);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteSessionFileAsync(string destination, byte[] bytes, bool overwrite, CancellationToken cancellationToken)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destination, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static PiSessionSnapshot CreateSessionSnapshot(ThreadId threadId, string path, PiSessionDocument document, int offset = 0, int limit = 1000)
    {
        var branch = document.Branch();
        var activeIds = branch.Select(entry => PiSessionDocument.Text(entry, "id")!).ToHashSet(StringComparer.Ordinal);
        var depths = new Dictionary<string, int>(StringComparer.Ordinal);
        var rows = new List<PiSessionTreeEntry>();
        foreach (var entry in document.Entries)
        {
            var id = PiSessionDocument.Text(entry, "id")!;
            var parent = PiSessionDocument.Text(entry, "parentId");
            var depth = parent is null ? 0 : depths[parent] + 1;
            depths[id] = depth;
            if (depths.Count > offset && rows.Count < limit) rows.Add(new(id, parent, depth, entry["message"] is JsonObject message
                ? PiSessionDocument.Text(message, "role") ?? "message" : PiSessionDocument.Text(entry, "type")!,
                PiSessionDocument.Preview(entry), activeIds.Contains(id), PiSessionDocument.CanFork(entry)));
        }
        var assistants = branch.Where(entry => entry["message"] is JsonObject message && PiSessionDocument.Text(message, "role") == "assistant").ToArray();
        long? tokens = 0;
        decimal? cost = 0;
        foreach (var assistant in assistants)
        {
            if (assistant["message"]?["usage"]?["totalTokens"] is JsonValue total && total.TryGetValue<long>(out var value)) tokens += value; else tokens = null;
            if (assistant["message"]?["usage"]?["cost"]?["total"] is JsonValue price && price.TryGetValue<decimal>(out var amount)) cost += amount; else cost = null;
        }
        var configuration = document.Configuration();
        return new(threadId, path, document.Revision, document.LeafId, rows, document.Entries.Count,
            branch.Count(entry => PiSessionDocument.Text(entry, "type") == "message"),
            configuration.Provider is { } provider && configuration.Model is { } model ? new(provider, model) : null,
            configuration.Thinking, tokens, cost, offset + rows.Count < document.Entries.Count,
            offset + rows.Count < document.Entries.Count ? offset + rows.Count : null);
    }
}
