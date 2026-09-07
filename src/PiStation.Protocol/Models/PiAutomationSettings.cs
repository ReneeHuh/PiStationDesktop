namespace PiStation.Protocol.Models;

// Null releases PiStation control; it does not rewrite Pi's existing shared settings.
public sealed record PiAutomationSettings(bool? AutoCompaction = null, bool? AutoRetry = null, long Revision = 0);

public sealed record PiAutomationStatus(PiAutomationSettings Saved, long? AppliedRevision,
    bool? VerifiedAutoCompaction, bool? AcknowledgedAutoRetry, string Message);
