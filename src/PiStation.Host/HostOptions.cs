using PiStation.PiRpc.Discovery;
using PiStation.Protocol.Models;

namespace PiStation.Host;

public sealed record HostOptions
{
    public required string ApplicationDataRoot { get; init; }

    public string EnvironmentName { get; init; } = "Local";

    public PiInstallation? PiInstallation { get; set; }

    public PiExtensionConfiguration Extensions { get; set; } = new();

    public IReadOnlyList<string> AdditionalPiArguments { get; init; } = [];

    public string? BrowserAutomationExtensionPath { get; init; }
    public string? ManagementExtensionPath { get; init; }
    public PiLaunchConfiguration LaunchConfiguration { get; set; } = new();
    public string? PlanExtensionPath { get; init; }
    public string? AgentExtensionPath { get; init; }

    public string? BrowserAutomationRoot { get; init; }

    public TimeSpan IdleRuntimeTimeout { get; init; } = TimeSpan.FromMinutes(30);

    public TimeSpan IdleRuntimeSweepInterval { get; init; } = TimeSpan.FromMinutes(1);

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
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(IdleRuntimeTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(IdleRuntimeSweepInterval, TimeSpan.Zero);
        ArgumentException.ThrowIfNullOrWhiteSpace(EnvironmentName);
        if (BrowserAutomationExtensionPath is not null && !File.Exists(BrowserAutomationExtensionPath))
        {
            throw new FileNotFoundException(
                "The Pi Station browser extension could not be found.",
                BrowserAutomationExtensionPath);
        }

        if (BrowserAutomationExtensionPath is not null && string.IsNullOrWhiteSpace(BrowserAutomationRoot))
        {
            throw new ArgumentException("Browser automation requires a bridge root.");
        }
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
