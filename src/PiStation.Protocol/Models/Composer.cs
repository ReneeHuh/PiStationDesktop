using PiStation.Protocol.Identifiers;

namespace PiStation.Protocol.Models;

public sealed record ComposerContext(
    string Id,
    string Kind,
    string Label,
    string Text,
    ThreadId? SourceThreadId = null,
    string? MessageId = null,
    string? RelativePath = null,
    int? StartLine = null,
    int? EndLine = null,
    string? SourceTextSha256 = null,
    string? Comment = null);

public static class ComposerContextDefaults
{
    public static string AppendToPrompt(string prompt, IReadOnlyList<ComposerContext>? context)
    {
        if (context is null || context.Count == 0) return prompt;
        static string Escape(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
        return prompt + "\n\n" + string.Join("\n\n", context.Select(chip =>
            $"<pistation_context kind=\"{Escape(chip.Kind)}\" label=\"{Escape(chip.Label)}\">\n{chip.Text}\n</pistation_context>" +
            (string.IsNullOrWhiteSpace(chip.Comment) ? "" : $"\nUser comment on this citation:\n{chip.Comment}")));
    }

    public const int MaximumItems = 32;
    public const int MaximumTextCharacters = 64 * 1024;

    public static void Validate(IReadOnlyList<ComposerContext>? context)
    {
        if (context is null) return;
        if (context.Count > MaximumItems || context.Sum(static item => (long)(item?.Text?.Length ?? 0) + (item?.Comment?.Length ?? 0)) > MaximumTextCharacters ||
            context.Any(static item => item is null || item.Text is null || item.Label is null || string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 128 ||
                string.IsNullOrWhiteSpace(item.Kind) || item.Kind.Length > 64 || item.Label.Length > 1024 || item.Comment?.Length > 8192 ||
                item.StartLine is <= 0 || item.EndLine is <= 0 || item.EndLine < item.StartLine ||
                (item.SourceTextSha256 is { } hash && (hash.Length != 64 || !hash.All(Uri.IsHexDigit)))) ||
            context.Select(static item => item.Id).Distinct(StringComparer.Ordinal).Count() != context.Count)
        {
            throw new ArgumentException("Review context exceeds its limits or contains invalid source information.", nameof(context));
        }
    }
}

public enum ComposerCommandSource
{
    BuiltIn,
    Extension,
    Prompt,
    Skill,
}

public sealed record ComposerCommandDescriptor(
    string Name,
    string Description,
    ComposerCommandSource Source,
    string? Location = null,
    string? Path = null,
    ComposerCommandSourceInfo? SourceInfo = null);

public sealed record ComposerCommandSourceInfo(string? Path, string? Source, string? Scope, string? Origin, string? BaseDir);

public sealed record ComposerDiscoveryResult(
    IReadOnlyList<ComposerCommandDescriptor> Commands,
    DateTimeOffset RefreshedUtc);

public sealed record ContextCompactionResult(
    string Summary,
    string? FirstKeptEntryId,
    long TokensBefore,
    long? EstimatedTokensAfter,
    TokenCostUsage? Usage);

public sealed record TokenCostUsage(
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long TotalTokens,
    decimal? TotalCost);

public sealed record PromptStash(
    string StashId,
    ProjectId ProjectId,
    ThreadId? ThreadId,
    string Title,
    string Text,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    IReadOnlyList<ComposerContext>? Context = null,
    IReadOnlyList<DraftAttachment>? Attachments = null);

public sealed record SavePromptStashRequest(
    ProjectId ProjectId,
    ThreadId? ThreadId,
    string Text,
    string? Title = null,
    string? StashId = null,
    DraftId? DraftId = null,
    long? ExpectedRevision = null);

public sealed record DeletePromptStashRequest(string StashId);
