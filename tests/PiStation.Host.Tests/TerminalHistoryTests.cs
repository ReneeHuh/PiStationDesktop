using System.Text;
using PiStation.Host.Terminals;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Tests;

public sealed class TerminalHistoryTests
{
    [Fact]
    public void HistoryBoundsLinesBytesAndCharactersWithoutSplittingUnicode()
    {
        var random = new Random(47);
        var history = new TerminalHistory(53, 5, 65);
        var expected = "";
        string[] units = ["a", "雪", "🚀", "\r\n", "abcdef", "\x1b[31m"];
        for (var index = 0; index < 2000; index++)
        {
            var text = units[random.Next(units.Length)];
            history.Append(text);
            expected += text;
            while (expected.Length > 53 || Encoding.UTF8.GetByteCount(expected) > 65 ||
                expected.Count(c => c == '\n') + (expected.EndsWith('\n') ? 0 : 1) > 5)
                expected = expected[(char.IsSurrogatePair(expected, 0) ? 2 : 1)..];
            Assert.Equal(expected, history.ToString());
        }
        history.Clear();
        history.Append("\ud83d"); history.Append("\ude80");
        Assert.Equal("🚀", history.ToString());
        var lines = new TerminalHistory(100_000);
        lines.Append(string.Concat(Enumerable.Range(0, 5100).Select(i => $"{i}\n")));
        Assert.StartsWith("100\n", lines.ToString());
        Assert.Equal(5000, lines.ToString().Count(c => c == '\n'));
    }

    [Fact]
    public void HistoryTrimsLargeChunksAndSurrogatePairsAtByteBoundary()
    {
        var history = new TerminalHistory(20_000, byteLimit: 10);
        history.Append(new string('x', 20_000) + "🚀🚀🚀");
        Assert.Equal("🚀🚀", history.ToString());
        history.Clear();
        history.Append(new string('x', 4095) + "🚀");
        Assert.Equal("xxxxxx🚀", history.ToString());
    }

    [Fact]
    public void ReplaySanitizerHandlesEveryTransportSplitAndPreservesVisualControls()
    {
        const string visual = "\x1b[31mred\x1b[0m\r\n\x1b[2J\x1b[H\x1b]0;title\a\x1b[?25l";
        const string queries = "\x1b[6n\x1b[?6n\x1b[1;2R\x1b[c\x1b[>0c\x1b[?25$p\x1b[?25;1$y\x1b[>q\x1b[?u\x1bP$qm\x1b\\\x1bP1$r0m\x1b\\\x1b]10;?\a\x1b]11;rgb:ffff/ffff/ffff\x1b\\\u009b6n";
        var input = "before" + queries + visual + "after";
        for (var split = 0; split <= input.Length; split++)
        {
            var sanitizer = new TerminalHistorySanitizer();
            Assert.Equal("before" + visual + "after", sanitizer.Append(input[..split]) + sanitizer.Append(input[split..]));
        }
        var oneAtATime = new TerminalHistorySanitizer();
        Assert.Equal("before" + visual + "after", string.Concat(input.Select(c => oneAtATime.Append(c.ToString()))));
        var oversized = new TerminalHistorySanitizer();
        Assert.Equal("safe", oversized.Append("\x1b]" + new string('x', 9000) + "\a" + "safe"));
    }

    [Fact]
    public async Task ClearBroadcastsSnapshotAndOldEpochRequiresHydration()
    {
        using var directory = new HostTestDirectory();
        var descriptor = Descriptor(ProjectId.New());
        var journal = new TerminalOutputJournal(descriptor, directory.CreateOptions());
        journal.CommitOutput("saved\x1b[6n");
        await using var stream = journal.SubscribeAsync(new(journal.Descriptor.Sequence, "previous-host")).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal("saved", Assert.IsType<TerminalSnapshotEnvelope>(stream.Current).BufferedOutput);
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(descriptor.Epoch, Assert.IsType<TerminalSynchronizedEnvelope>(stream.Current).Epoch);
        var cleared = journal.ClearHistory();
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(cleared, stream.Current);
        Assert.Empty(cleared.BufferedOutput);
        journal.CommitOutput("new");
        Assert.Equal("new", journal.Snapshot().BufferedOutput);
    }

    [Fact]
    public void ActivityChangesAdvanceSequenceOnlyWhenNeededAndExitClearsLabels()
    {
        using var directory = new HostTestDirectory();
        var journal = new TerminalOutputJournal(Descriptor(ProjectId.New()), directory.CreateOptions());
        journal.CommitActivity(new(true, "node"));
        var sequence = journal.Descriptor.Sequence;
        journal.CommitActivity(new(true, "node"));
        Assert.Equal(sequence, journal.Descriptor.Sequence);
        Assert.Equal("node", journal.Descriptor.ForegroundCommand);
        journal.CommitActivity(new(false, null));
        Assert.False(journal.Descriptor.HasRunningSubprocess);
        Assert.Equal(sequence.Next(), journal.Descriptor.Sequence);
        journal.CommitState(TerminalSessionState.Exited, 0);
        journal.CommitActivity(new(true, "late-child"));
        Assert.False(journal.Descriptor.HasRunningSubprocess);
        Assert.Null(journal.Descriptor.ForegroundCommand);
    }

    [Fact]
    public async Task AtomicHistoryFilesRoundTripAndRejectDamagedOrMismatchedMetadata()
    {
        using var directory = new HostTestDirectory();
        var storage = new TerminalHistoryStore(directory.Path);
        var snapshot = new TerminalSnapshotEnvelope(Descriptor(ProjectId.New()), "雪🚀\x1b[31mred");
        await storage.SaveAsync(snapshot);
        var file = Assert.Single(storage.Files());
        Assert.Equal(snapshot, await storage.ReadAsync(file, default));
        await storage.SaveAsync(snapshot with { BufferedOutput = "replacement" });
        Assert.Equal("replacement", (await storage.ReadAsync(file, default))!.BufferedOutput);
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(file)!, "*.tmp"));
        await File.WriteAllTextAsync(file, "{}");
        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => storage.ReadAsync(file, default));
        await File.WriteAllTextAsync(file, """{"descriptor":null,"bufferedOutput":""}""");
        await Assert.ThrowsAnyAsync<Exception>(() => storage.ReadAsync(file, default));
        await storage.SaveAsync(snapshot);
        var wrongFile = Path.Combine(Path.GetDirectoryName(file)!, "wrong.json");
        File.Copy(file, wrongFile);
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.ReadAsync(wrongFile, default));
        storage.Delete(snapshot.TerminalSessionId);
        Assert.False(File.Exists(file));
    }

    internal static TerminalSessionDescriptor Descriptor(ProjectId project) => new(TerminalSessionId.New(), project,
        "Command Prompt 1", TerminalShellKind.CommandPrompt, "Command Prompt", TerminalSessionState.Running,
        100, 30, null, null, DateTimeOffset.UtcNow, Sequence.Initial, Epoch: Guid.NewGuid().ToString("N"));
}
