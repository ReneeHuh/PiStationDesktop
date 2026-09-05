using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;

namespace PiStation.ClientRuntime;

public sealed record DraftAttachmentRemoveResult(CommandReceipt Receipt, ThreadDraft? Draft);
