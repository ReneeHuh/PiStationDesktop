using System.Diagnostics;
using PiStation.Protocol;
using PiStation.Protocol.Commands;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Tests;

public sealed class CheckpointIntegrationTests
{
    [Fact]
    public async Task SettledTurnCapturesCheckpointAndConfirmedCommandRewindsWorkspaceAndConversation()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var projectRoot = temporaryDirectory.CreateDirectory("project");
        InitializeRepository(projectRoot);
        await using var environment = await EnvironmentService.CreateAsync(temporaryDirectory.CreateOptions());
        var descriptor = environment.GetDescriptor();
        Assert.Contains("checkpoint.read", descriptor.Capabilities);
        Assert.Contains("checkpoint.revert", descriptor.Capabilities);
        var project = await environment.AddProjectAsync(new AddProjectRequest(projectRoot));
        var thread = await environment.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        var clientId = ClientId.New();
        var startCommandId = CommandId.New();
        // This correctness test launches two turns and multiple real Git subprocesses;
        // leave headroom when the complete solution suite is running alongside builds.
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await environment.ExecuteThreadCommandAsync(new ExecuteThreadCommandRequest(
            ProtocolVersion.Current,
            descriptor.EnvironmentId,
            clientId,
            startCommandId,
            thread.ThreadId,
            null,
            null,
            new ThreadStartTurnCommand("create a checkpoint")), cancellation.Token);
        var settledReceipt = await WaitForReceiptAsync(
            environment,
            clientId,
            startCommandId,
            cancellation.Token);
        Assert.Equal(CommandReceiptState.Completed, settledReceipt.State);
        var firstSettled = await ReadSnapshotAsync(environment, thread.ThreadId, cancellation.Token);
        var firstCheckpoint = Assert.Single(firstSettled.Projection.Checkpoints);
        Assert.Equal(ThreadCheckpointStatus.Ready, firstCheckpoint.Status);
        Assert.Equal(1, firstCheckpoint.TurnCount);
        Assert.NotNull(firstCheckpoint.PiEntryIdAfterTurn);

        var secondCommandId = CommandId.New();
        await environment.ExecuteThreadCommandAsync(new ExecuteThreadCommandRequest(
            ProtocolVersion.Current,
            descriptor.EnvironmentId,
            clientId,
            secondCommandId,
            thread.ThreadId,
            firstSettled.ProjectionEpoch,
            null,
            new ThreadStartTurnCommand("create another checkpoint")), cancellation.Token);
        Assert.Equal(
            CommandReceiptState.Completed,
            (await WaitForReceiptAsync(environment, clientId, secondCommandId, cancellation.Token)).State);
        var settled = await ReadSnapshotAsync(environment, thread.ThreadId, cancellation.Token);
        Assert.Equal(2, settled.Projection.Checkpoints.Count);
        var secondCheckpoint = settled.Projection.Checkpoints.Single(static checkpoint => checkpoint.TurnCount == 2);
        Assert.Equal(firstCheckpoint.PiEntryIdAfterTurn, secondCheckpoint.PiEntryIdBeforeTurn);
        Assert.Equal(ThreadCheckpointStatus.Ready, secondCheckpoint.Status);

        await File.WriteAllTextAsync(Path.Combine(projectRoot, "README.md"), "discard this edit\n", cancellation.Token);
        var revertReceipt = await environment.ExecuteThreadCommandAsync(new ExecuteThreadCommandRequest(
            ProtocolVersion.Current,
            descriptor.EnvironmentId,
            clientId,
            CommandId.New(),
            thread.ThreadId,
            settled.ProjectionEpoch,
            null,
            new ThreadRevertCheckpointCommand(1)), cancellation.Token);

        Assert.Equal(CommandReceiptState.Completed, revertReceipt.State);
        Assert.Equal("baseline\n", (await File.ReadAllTextAsync(
            Path.Combine(projectRoot, "README.md"),
            cancellation.Token)).ReplaceLineEndings("\n"));
        var rewound = await ReadSnapshotAsync(environment, thread.ThreadId, cancellation.Token);
        Assert.Equal(2, rewound.Projection.Timeline.OfType<PiStation.Protocol.Projections.MessageTimelineItem>().Count());
        Assert.Equal(1, Assert.Single(rewound.Projection.Checkpoints).TurnCount);
        Assert.Equal(firstCheckpoint.PiEntryIdAfterTurn, rewound.Projection.LastEntryId);
    }

    private static async Task<CommandReceipt> WaitForReceiptAsync(
        EnvironmentService environment,
        ClientId clientId,
        CommandId commandId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var receipt = await environment.GetCommandReceiptAsync(clientId, commandId, cancellationToken);
            if (receipt?.State is CommandReceiptState.Completed or
                                  CommandReceiptState.Rejected or
                                  CommandReceiptState.Failed or
                                  CommandReceiptState.DispatchUncertain)
            {
                return receipt;
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    private static async Task<ThreadSnapshotEnvelope> ReadSnapshotAsync(
        EnvironmentService environment,
        ThreadId threadId,
        CancellationToken cancellationToken)
    {
        await foreach (var envelope in environment.SubscribeThreadAsync(threadId, null, cancellationToken))
        {
            return Assert.IsType<ThreadSnapshotEnvelope>(envelope);
        }

        throw new InvalidOperationException("The thread stream ended before its snapshot.");
    }

    private static void InitializeRepository(string projectRoot)
    {
        RunGit(projectRoot, "init", "--quiet", "--initial-branch=main");
        RunGit(projectRoot, "config", "user.email", "pistation@example.invalid");
        RunGit(projectRoot, "config", "user.name", "Pi Station Tests");
        File.WriteAllText(Path.Combine(projectRoot, "README.md"), "baseline\n");
        RunGit(projectRoot, "add", "README.md");
        RunGit(projectRoot, "commit", "--quiet", "-m", "baseline");
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Git did not start.");
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, standardError);
    }
}
