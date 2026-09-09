using PiStation.PiRpc.Discovery;
using PiStation.PiRpc.Process;
using PiStation.PiRpc.Transport;

namespace PiStation.PiRpc.Tests;

public sealed class PiShellTests
{
    [Fact]
    public async Task ShellStreamsByRequestIdAndWaitsForExplicitCancellation()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var process = await FakePiTestHost.StartAsync(directory, connectionOptions:
            new PiRpcConnectionOptions { DefaultCommandTimeout = TimeSpan.FromSeconds(2) }, cancellationToken: timeout.Token);
        var chunk = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = process.Connection.ExecuteBashAsync("wait", true, value => chunk.TrySetResult(value), () => { }, timeout.Token);
        Assert.Equal("started 😀\n", await chunk.Task.WaitAsync(timeout.Token));
        await Task.Delay(TimeSpan.FromSeconds(2.2), timeout.Token);
        Assert.False(run.IsCompleted);
        await process.Connection.AbortBashAsync(timeout.Token);
        Assert.True((await run).Cancelled);
    }

    [RealPiOfflineFact]
    [Trait("Category", "RealPiOffline")]
    public async Task RealPiStreamsCancelsAndHonorsContextExclusion()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var installation = await new PiLocator().LocateAsync(new PiLocatorOptions
        {
            ExplicitPiPath = Environment.GetEnvironmentVariable("PISTATION_PI_PATH"),
        }, timeout.Token);
        var trace = directory.GetPath("context.jsonl");
        await using var process = await PiProcessLauncher.StartAsync(new PiProcessLaunchOptions
        {
            Installation = installation, ProjectDirectory = directory.CreateDirectory("project"),
            SessionDirectory = directory.CreateDirectory("sessions"), SessionId = Guid.NewGuid().ToString(),
            AdditionalArguments = ["--extension", Path.Combine(AppContext.BaseDirectory, "Fixtures", "offline-provider.ts"),
                "--provider", "pistation-offline", "--model", "deterministic"],
            EnvironmentVariables = new Dictionary<string, string?>
            {
                ["PI_CODING_AGENT_DIR"] = directory.CreateDirectory("agent"), ["PISTATION_TEST_TRACE"] = trace,
            },
        }, timeout.Token);
        var output = new System.Text.StringBuilder();
        var included = await process.Connection.ExecuteBashAsync("printf 'INCLUDED_SHELL_SENTINEL'", false,
            value => output.Append(value), () => { }, timeout.Token);
        Assert.Equal(0, included.ExitCode);
        Assert.Contains("INCLUDED_SHELL_SENTINEL", output.ToString());
        await process.Connection.ExecuteBashAsync("printf 'EXCLUDED_SHELL_SENTINEL'", true, _ => { }, () => { }, timeout.Token);
        var chunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = process.Connection.ExecuteBashAsync("printf 'CANCEL_READY'; sleep 30", true,
            _ => chunk.TrySetResult(), () => { }, timeout.Token);
        await chunk.Task.WaitAsync(timeout.Token);
        await process.Connection.AbortBashAsync(timeout.Token);
        Assert.True((await cancelled).Cancelled);
        await process.Connection.PromptAsync("Acknowledge the shell result.", timeout.Token);
        await FakePiTestHost.ReadUntilSettledAsync(process.Connection, timeout.Token);
        var context = await File.ReadAllTextAsync(trace, timeout.Token);
        Assert.Contains("INCLUDED_SHELL_SENTINEL", context);
        Assert.DoesNotContain("EXCLUDED_SHELL_SENTINEL", context);
        Assert.DoesNotContain("CANCEL_READY", context);
        var entries = await process.Connection.GetEntriesAsync(cancellationToken: timeout.Token);
        Assert.Equal(3, entries.Entries.Count(entry => entry.TryGetProperty("message", out var message) &&
            message.GetProperty("role").GetString() == "bashExecution"));
    }
}
