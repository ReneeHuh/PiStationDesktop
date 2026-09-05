using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;

namespace PiStation.ClientRuntime;

public sealed record ThreadDraftSaveResult(CommandReceipt Receipt, ThreadDraft? Draft);
