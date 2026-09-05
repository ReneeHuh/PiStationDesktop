using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;

namespace PiStation.ClientRuntime;

public sealed record ThreadDraftClearResult(CommandReceipt Receipt, ThreadDraft? Draft);
