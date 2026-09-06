using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Receipts;

namespace PiStation.Protocol.Models;

public sealed record HostingOperation(
    CommandId OperationId,
    string Action,
    CommandReceiptState State,
    SourceControlOperationResult? Result,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
