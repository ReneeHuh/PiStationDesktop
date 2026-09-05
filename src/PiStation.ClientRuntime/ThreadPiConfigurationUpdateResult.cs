using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;

namespace PiStation.ClientRuntime;

public sealed record ThreadPiConfigurationUpdateResult(
    CommandReceipt Receipt,
    ThreadPiConfigurationSnapshot? Snapshot);
