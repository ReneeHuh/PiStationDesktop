using PiStation.PiRpc.Discovery;
using PiStation.Protocol.Models;

namespace PiStation.Host;

public sealed record HostOptions
{
    public required string ApplicationDataRoot { get; init; }

    public string EnvironmentName { get; init; } = "Local";

    public PiInstallation? PiInstallation { get; init; }

    public IReadOnlyList<string> AdditionalPiArguments { get; init; } = [];

    public int JournalEventLimit { get; init; } = 512;

    public int JournalByteLimit { get; init; } = 2 * 1024 * 1024;

    public int SubscriberCapacity { get; init; } = 256;

    public int ToolOutputCharacterLimit { get; init; } = 32 * 1024;

    public int ToolArgumentCharacterLimit { get; init; } = 8 * 1024;

    public int MaximumAttachmentsPerDraft { get; init; } = AttachmentDefaults.MaximumPerDraft;

    public long MaximumImageAttachmentBytes { get; init; } = AttachmentDefaults.MaximumImageBytes;

    public long MaximumFileAttachmentBytes { get; init; } = AttachmentDefaults.MaximumFileBytes;

    public int MaximumFileSearchScannedFiles { get; init; } = 50_000;

    public int MaximumTerminalSessionsPerProject { get; init; } = 4;

    public int TerminalOutputCharacterLimit { get; init; } = 1024 * 1024;

    public string CanonicalDataRoot => Path.GetFullPath(ApplicationDataRoot);

    public string DatabasePath => Path.Combine(CanonicalDataRoot, "host.db");

    public string SessionRoot => Path.Combine(CanonicalDataRoot, "sessions");

    public string AttachmentRoot => Path.Combine(CanonicalDataRoot, "attachments");

    public string AttachmentStagingRoot => Path.Combine(CanonicalDataRoot, "attachment-staging");

    public string WorktreeRoot => Path.Combine(CanonicalDataRoot, "worktrees");

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ApplicationDataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(EnvironmentName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(JournalEventLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(JournalByteLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(SubscriberCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ToolOutputCharacterLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ToolArgumentCharacterLimit);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumAttachmentsPerDraft);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumImageAttachmentBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumFileAttachmentBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumFileSearchScannedFiles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumTerminalSessionsPerProject);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TerminalOutputCharacterLimit);
        if (MaximumImageAttachmentBytes > MaximumFileAttachmentBytes)
        {
            throw new ArgumentException("The image attachment limit cannot exceed the generic file limit.");
        }
    }
}
