using PiStation.Host.Persistence;
using PiStation.Host.Projects;
using PiStation.Host.Terminals;
using PiStation.Protocol.Models;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Tests;

public sealed class TerminalPersistenceTests
{
    [Fact]
    public async Task RestoreBoundsInactiveSessionsAndSkipsCorruptAndOrphanedHistory()
    {
        using var directory = new HostTestDirectory();
        var options = directory.CreateOptions() with { TerminalOutputCharacterLimit = 32 };
        var database = new HostDatabase(options); await database.InitializeAsync();
        var project = await new ProjectService(database).AddAsync(new(directory.CreateDirectory("project")));
        var storage = new TerminalHistoryStore(options.CanonicalDataRoot);
        for (var index = 0; index < 130; index++)
            await storage.SaveAsync(new(TerminalHistoryTests.Descriptor(project.ProjectId), new string('x', 100)));
        var orphan = TerminalHistoryTests.Descriptor(PiStation.Protocol.Identifiers.ProjectId.New());
        await storage.SaveAsync(new(orphan, "orphan"));
        await File.WriteAllTextAsync(Path.Combine(options.CanonicalDataRoot, "terminal-history", "broken.json"), "{");
        await using var registry = new TerminalSessionRegistry(database, options);
        var sessions = await registry.ListAsync(project.ProjectId, default);
        Assert.Equal(128, sessions.Count); Assert.Equal(0, registry.ActiveCount);
        Assert.All(sessions, item => Assert.Equal(TerminalSessionState.Interrupted, item.State));
        await using var stream = registry.SubscribeAsync(sessions[0].TerminalSessionId, null, default).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        Assert.Equal(32, Assert.IsType<TerminalSnapshotEnvelope>(stream.Current).BufferedOutput.Length);
        Assert.Equal(129, storage.Files().Count()); // 128 valid histories plus the untouched damaged file.
    }

    [Fact]
    public async Task ConPtyHistoryAndMetadataSurviveHostRestartWhileClearAndCloseStayCleared()
    {
        using var directory = new HostTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var options = directory.CreateOptions();
        var database = new HostDatabase(options); await database.InitializeAsync(token);
        var project = await new ProjectService(database).AddAsync(new(directory.CreateDirectory("project")), token);
        TerminalSessionDescriptor running, exited;
        await using (var registry = new TerminalSessionRegistry(database, options))
        {
            running = await registry.StartAsync(new(project.ProjectId, TerminalShellKind.CommandPrompt, 101, 31), token);
            exited = await registry.StartAsync(new(project.ProjectId, TerminalShellKind.CommandPrompt), token);
            var output = WaitForOutputAsync(registry, running.TerminalSessionId, "persisted-output", token);
            await registry.WriteAsync(new(running.TerminalSessionId, "echo persisted-^output\r"), token);
            await output;
            await registry.WriteAsync(new(exited.TerminalSessionId, "echo second-history & exit 7\r"), token);
            Assert.Equal(7, (await registry.WaitForExitAsync(exited.TerminalSessionId, token)).ExitCode);
        }
        await using (var restored = new TerminalSessionRegistry(database, options))
        {
            var sessions = await restored.ListAsync(project.ProjectId, token);
            Assert.Equal(2, sessions.Count); Assert.Equal(0, restored.ActiveCount);
            var first = Assert.Single(sessions, item => item.TerminalSessionId == running.TerminalSessionId);
            Assert.Equal(TerminalSessionState.Interrupted, first.State);
            Assert.Equal(running.Name, first.Name); Assert.Equal(running.CreatedUtc, first.CreatedUtc);
            Assert.Equal(101, first.Columns); Assert.Equal(31, first.Rows);
            Assert.Equal(project.CanonicalPath, first.WorkspacePath);
            Assert.NotEqual(running.Epoch, first.Epoch);
            await using var stream = restored.SubscribeAsync(first.TerminalSessionId, new(first.Sequence, running.Epoch), token).GetAsyncEnumerator(token);
            Assert.True(await stream.MoveNextAsync());
            Assert.Contains("persisted-output", Assert.IsType<TerminalSnapshotEnvelope>(stream.Current).BufferedOutput);
            var second = Assert.Single(sessions, item => item.TerminalSessionId == exited.TerminalSessionId);
            Assert.Equal(TerminalSessionState.Exited, second.State); Assert.Equal(7, second.ExitCode);
            Assert.Empty((await restored.ClearHistoryAsync(new(first.TerminalSessionId), token)).BufferedOutput);
            await restored.CloseAsync(new(second.TerminalSessionId), token);
        }
        await using (var again = new TerminalSessionRegistry(database, options))
        {
            var only = Assert.Single(await again.ListAsync(project.ProjectId, token));
            Assert.Equal(running.TerminalSessionId, only.TerminalSessionId);
            await using var stream = again.SubscribeAsync(only.TerminalSessionId, null, token).GetAsyncEnumerator(token);
            Assert.True(await stream.MoveNextAsync());
            Assert.Empty(Assert.IsType<TerminalSnapshotEnvelope>(stream.Current).BufferedOutput);
            await again.CloseAsync(new(only.TerminalSessionId), token);
        }
        Assert.Empty(new TerminalHistoryStore(options.CanonicalDataRoot).Files());
    }

    [Fact]
    public async Task NativeSnapshotDetectsARealChildAndReturnsToIdleWithoutPollingHelpers()
    {
        using var directory = new HostTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        await using var process = ConPtyTerminalProcess.Start(directory.Path, TerminalShellKind.CommandPrompt, 100, 30);
        // Drain ConPTY continuously, including while its native handles close.
        var drain = process.Output.CopyToAsync(Stream.Null, token);
        try
        {
            // Let the child exit normally: this test exercises activity inspection,
            // while the existing hub test covers Ctrl+C after a command is ready.
            await process.WriteAsync("ping -n 6 127.0.0.1\r", token);
            await UntilAsync(() => string.Equals(TerminalProcessInspector.Inspect(TerminalProcessInspector.Capture(), process.ProcessId)?.Command, "ping", StringComparison.OrdinalIgnoreCase), token);
            await UntilAsync(() => TerminalProcessInspector.Inspect(TerminalProcessInspector.Capture(), process.ProcessId)?.HasChildren == false, token);
        }
        finally
        {
            await process.StopAsync(CancellationToken.None);
            try { await drain; } catch (IOException) { } catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }
    }

    [Fact]
    public void ProcessInspectionUsesDirectChildrenAndTreatsMissingShellAsUnknown()
    {
        TerminalProcessEntry[] entries = [new(10, 1, "cmd.exe"), new(30, 10, "node.exe"), new(31, 30, "worker.exe"), new(40, 1, "other.exe")];
        Assert.Equal(new(true, "node"), TerminalProcessInspector.Inspect(entries, 10));
        Assert.Equal(new(false, null), TerminalProcessInspector.Inspect(entries, 40));
        Assert.Null(TerminalProcessInspector.Inspect(entries, 99));
    }

    private static async Task WaitForOutputAsync(TerminalSessionRegistry registry, PiStation.Protocol.Identifiers.TerminalSessionId id, string marker, CancellationToken token)
    {
        var output = new System.Text.StringBuilder();
        await foreach (var envelope in registry.SubscribeAsync(id, null, token))
        {
            if (envelope is TerminalSnapshotEnvelope snapshot) output.Append(snapshot.BufferedOutput);
            if (envelope is TerminalOutputEnvelope chunk) output.Append(chunk.Text);
            if (output.ToString().Contains(marker, StringComparison.Ordinal)) return;
        }
        Assert.Fail("Terminal stream ended before the marker.");
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(50, token);
    }
}
