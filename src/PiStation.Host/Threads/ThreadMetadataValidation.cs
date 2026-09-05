using PiStation.Host.Errors;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Models;

namespace PiStation.Host.Threads;

internal static class ThreadMetadataValidation
{
    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.ThreadInvalid,
                "A thread title cannot be empty.");
        }

        var normalized = title.Trim();
        if (normalized.Length > ThreadLifecycleDefaults.MaximumTitleLength)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.ThreadInvalid,
                $"A thread title cannot exceed {ThreadLifecycleDefaults.MaximumTitleLength} characters.");
        }

        if (normalized.Any(char.IsControl))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.ThreadInvalid,
                "A thread title cannot contain control characters.");
        }

        return normalized;
    }

    public static string NormalizeSearchQuery(string? query)
    {
        if (query is null)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.ThreadSearchInvalid,
                "A thread search query is required.");
        }

        var normalized = query.Trim();
        if (normalized.Length > ThreadLifecycleDefaults.MaximumSearchQueryLength)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.ThreadSearchInvalid,
                $"A thread search query cannot exceed {ThreadLifecycleDefaults.MaximumSearchQueryLength} characters.");
        }

        if (normalized.Any(char.IsControl))
        {
            throw new HostOperationException(
                ProtocolErrorCodes.ThreadSearchInvalid,
                "A thread search query cannot contain control characters.");
        }

        return normalized;
    }
}
