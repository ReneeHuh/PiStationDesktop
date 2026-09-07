namespace PiStation.Protocol.Models;

public sealed record SettlementSettings(int? InactiveDays = 3, bool OnMerge = true, bool OnClose = true);
