using PiStation.Protocol.Identifiers;

namespace PiStation.Protocol.Receipts;

public enum CommandReceiptState
{
    Received,
    Dispatching,
    Accepted,
    Completed,
    Rejected,
    Failed,
    DispatchUncertain,
}

public sealed record CommandReceipt(
    EnvironmentId EnvironmentId,
    ClientId ClientId,
    CommandId CommandId,
    ThreadId ThreadId,
    CommandReceiptState State,
    string? ErrorCode,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
