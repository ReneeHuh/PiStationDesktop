using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Streaming;

namespace PiStation.ClientRuntime.Tests;

public sealed class TerminalStoreTests
{
    [Fact]
    public void SnapshotOutputAndStateAdvanceOneReplayableProjection()
    {
        var terminalSessionId = TerminalSessionId.Parse("terminal-1");
        var store = new TerminalStore(terminalSessionId);
        var changes = new List<TerminalChangedEventArgs>();
        store.Changed += (_, args) => changes.Add(args);
        var descriptor = Descriptor(terminalSessionId, Sequence.Initial);

        Assert.Equal(
            ProjectionApplyResult.Applied,
            store.Apply(new TerminalSnapshotEnvelope(descriptor, "ready> ")));
        Assert.Equal(
            ProjectionApplyResult.Applied,
            store.Apply(new TerminalOutputEnvelope(terminalSessionId, new Sequence(1), "echo hello\r\n")));
        var exited = descriptor with
        {
            State = TerminalSessionState.Exited,
            ExitCode = 0,
            Sequence = new Sequence(2),
        };
        Assert.Equal(
            ProjectionApplyResult.Applied,
            store.Apply(new TerminalStateEnvelope(exited)));

        Assert.Equal(exited, store.Descriptor);
        Assert.Equal("ready> echo hello\r\n", store.Output);
        Assert.Equal(new TerminalCursor(new Sequence(2)), store.Cursor);
        Assert.Equal(3, changes.Count);
        Assert.True(changes[0].OutputWasReset);
        Assert.Null(changes[0].AppendedOutput);
        Assert.Equal("echo hello\r\n", changes[1].AppendedOutput);
        Assert.False(changes[1].OutputWasReset);
        Assert.Null(changes[2].AppendedOutput);
    }

    [Fact]
    public void SequenceGapRequiresSnapshotResynchronization()
    {
        var terminalSessionId = TerminalSessionId.Parse("terminal-gap");
        var store = new TerminalStore(terminalSessionId);
        store.Apply(new TerminalSnapshotEnvelope(Descriptor(terminalSessionId, Sequence.Initial), string.Empty));

        Assert.Equal(
            ProjectionApplyResult.ResyncRequired,
            store.Apply(new TerminalOutputEnvelope(terminalSessionId, new Sequence(2), "late")));

        store.Reset();
        Assert.Null(store.Descriptor);
        Assert.Empty(store.Output);
        Assert.Null(store.Cursor);
    }

    [Fact]
    public void EnvelopeForAnotherSessionIsRejected()
    {
        var store = new TerminalStore(TerminalSessionId.Parse("terminal-a"));
        var envelope = new TerminalSnapshotEnvelope(
            Descriptor(TerminalSessionId.Parse("terminal-b"), Sequence.Initial),
            string.Empty);

        Assert.Throws<ArgumentException>(() => store.Apply(envelope));
    }

    private static TerminalSessionDescriptor Descriptor(
        TerminalSessionId terminalSessionId,
        Sequence sequence) => new(
        terminalSessionId,
        ProjectId.Parse("project-1"),
        "Command Prompt 1",
        TerminalShellKind.CommandPrompt,
        "Command Prompt",
        TerminalSessionState.Running,
        100,
        30,
        null,
        null,
        new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero),
        sequence);
}
