using PiStation.PiRpc.Transport;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Host;

public sealed partial class EnvironmentService
{
    private async Task<string> RetainSentContentAsync(ThreadId threadId, string prompt,
        DraftId? draftId, long? revision, IReadOnlyList<PiPromptAttachment> attachments,
        CancellationToken cancellationToken)
    {
        if (draftId is null || revision is null) return prompt;
        var draft = await GetThreadDraftAsync(threadId, cancellationToken).ConfigureAwait(false);
        if (draft.DraftId != draftId || draft.Revision != revision)
            throw DraftConflict(revision.Value, draft.Revision);
        var ids = attachments.Select(a => a.AttachmentId).ToHashSet(StringComparer.Ordinal);
        var retained = draft.Attachments.Where(a => ids.Contains(a.AttachmentId.Value)).ToArray();
        var citations = draft.Context?.ToArray() ?? [];
        if (retained.Length == 0 && citations.Length == 0) return prompt;
        var content = new SentMessageContent(Guid.NewGuid().ToString("N"),
            PiPromptFormatter.CreateDisplayMessage(prompt, attachments), retained, citations);
        // Retain before dispatch: a timeout cannot prove that Pi did not consume this prompt.
        await _database.RetainSentMessageAsync(threadId, draftId.Value, revision.Value, content, cancellationToken)
            .ConfigureAwait(false);
        return SentMessageReference.Append(prompt, content.Id);
    }
}
