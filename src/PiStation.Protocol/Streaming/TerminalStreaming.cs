using System.Text.Json.Serialization;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Protocol.Streaming;

public sealed record TerminalCursor(Sequence Sequence);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(TerminalSnapshotEnvelope), "snapshot")]
[JsonDerivedType(typeof(TerminalOutputEnvelope), "output")]
[JsonDerivedType(typeof(TerminalStateEnvelope), "state")]
[JsonDerivedType(typeof(TerminalResyncRequiredEnvelope), "resyncRequired")]
[JsonDerivedType(typeof(TerminalSynchronizedEnvelope), "synchronized")]
public abstract record TerminalEnvelope(TerminalSessionId TerminalSessionId, Sequence Sequence);

public sealed record TerminalSnapshotEnvelope(
    TerminalSessionDescriptor Descriptor,
    string BufferedOutput) : TerminalEnvelope(Descriptor.TerminalSessionId, Descriptor.Sequence);

public sealed record TerminalOutputEnvelope(
    TerminalSessionId TerminalSessionId,
    Sequence Sequence,
    string Text) : TerminalEnvelope(TerminalSessionId, Sequence);

public sealed record TerminalStateEnvelope(
    TerminalSessionDescriptor Descriptor) : TerminalEnvelope(Descriptor.TerminalSessionId, Descriptor.Sequence);

public sealed record TerminalResyncRequiredEnvelope(
    TerminalSessionId TerminalSessionId,
    Sequence Sequence,
    string Reason) : TerminalEnvelope(TerminalSessionId, Sequence);

public sealed record TerminalSynchronizedEnvelope(TerminalSessionId TerminalSessionId, Sequence Sequence) : TerminalEnvelope(TerminalSessionId, Sequence);
