using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.ClientRuntime;

/// <summary>Local review drafts, including the identity of any write awaiting confirmation.</summary>
public sealed class PullRequestReviewDraftStore
{
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private readonly string _root;
    // Serialize file replacement across store instances in the desktop process.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public PullRequestReviewDraftStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = Path.GetFullPath(root);
    }

    public async Task<PullRequestReviewDraft?> LoadAsync(ProjectId projectId, string repository, string number, CancellationToken cancellationToken = default)
    {
        var file = FilePath(projectId, repository, number);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(file)) return null;
            await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumFileBytes) throw new InvalidDataException("The saved review draft exceeds its size limit. It has been retained for recovery.");
            PullRequestReviewDraft draft;
            try
            {
                draft = await JsonSerializer.DeserializeAsync(stream, ProtocolJsonContext.Default.PullRequestReviewDraft, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The saved review draft is empty.");
                Validate(draft);
            }
            catch (Exception exception) when (exception is JsonException or ArgumentException)
            {
                throw new InvalidDataException("The saved review draft is invalid. It has been retained; inspect it before starting another review operation.", exception);
            }
            if (!string.Equals(draft.Repository, repository, StringComparison.OrdinalIgnoreCase) || draft.Number != number)
                throw new InvalidDataException("The saved review draft belongs to another repository or pull request.");
            return draft;
        }
        finally { Gate.Release(); }
    }

    public async Task SaveAsync(ProjectId projectId, PullRequestReviewDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        Validate(draft);
        var file = FilePath(projectId, draft.Repository, draft.Number);
        var contents = JsonSerializer.SerializeToUtf8Bytes(draft, ProtocolJsonContext.Default.PullRequestReviewDraft);
        if (contents.Length > MaximumFileBytes) throw new ArgumentException("The review draft exceeds its size limit.", nameof(draft));
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(_root);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, file, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            finally { Gate.Release(); }
        }
    }

    public async Task DeleteAsync(ProjectId projectId, string repository, string number, CancellationToken cancellationToken = default)
    {
        var file = FilePath(projectId, repository, number);
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(file);
        }
        finally { Gate.Release(); }
    }

    private string FilePath(ProjectId projectId, string repository, string number)
    {
        ValidateKey(repository, number);
        if (string.IsNullOrWhiteSpace(projectId.Value)) throw new ArgumentException("A project identity is required.", nameof(projectId));
        // Length prefixes prevent ambiguous keys, and no caller-controlled text becomes a path.
        var project = projectId.Value;
        var repo = repository.ToUpperInvariant();
        var identity = $"{project.Length}:{project}{repo.Length}:{repo}{number.Length}:{number}";
        return Path.Combine(_root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".json");
    }

    private static void ValidateKey(string repository, string number)
    {
        if (string.IsNullOrWhiteSpace(repository) || repository.Length > 1024)
            throw new ArgumentException("A bounded repository identity is required.", nameof(repository));
        if (string.IsNullOrWhiteSpace(number) || number.Length > 20 || number.Any(character => character is < '0' or > '9') || number.All(character => character == '0'))
            throw new ArgumentException("A positive pull-request number is required.", nameof(number));
    }

    private static void Validate(PullRequestReviewDraft draft)
    {
        ValidateKey(draft.Repository, draft.Number);
        if (string.IsNullOrWhiteSpace(draft.HeadCommitId) || draft.HeadCommitId.Length > 128 || !Enum.IsDefined(draft.Event) ||
            draft.Body is null || draft.Body.Length > PullRequestReviewDefaults.MaximumBodyCharacters ||
            draft.ReplyBody is null || draft.ReplyBody.Length > PullRequestReviewDefaults.MaximumBodyCharacters ||
            draft.ReplyThreadId?.Length > 256 || draft.Comments is null || draft.Comments.Count > PullRequestReviewDefaults.MaximumInlineComments)
            throw new ArgumentException("The review draft contains an invalid value.", nameof(draft));
        if (draft.PendingOperationId is { } operation && (string.IsNullOrWhiteSpace(operation.Value) || operation.Value.Length > 128))
            throw new ArgumentException("The saved review operation identity is invalid.", nameof(draft));
        if (draft.PendingAction is not (null or "SubmitReview" or "Reply" or "Resolve" or "Unresolve"))
            throw new ArgumentException("The saved review operation action is invalid.", nameof(draft));
        foreach (var comment in draft.Comments)
            if (comment is null || string.IsNullOrWhiteSpace(comment.Path) || comment.Path.Length > 4096 || comment.Line <= 0 ||
                !Enum.IsDefined(comment.Side) || string.IsNullOrWhiteSpace(comment.Body) || comment.Body.Length > PullRequestReviewDefaults.MaximumBodyCharacters)
                throw new ArgumentException("An inline review comment is invalid.", nameof(draft));
    }
}
