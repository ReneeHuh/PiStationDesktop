using PiStation.PiRpc.Discovery;
using PiStation.PiRpc.Process;
using PiStation.PiRpc.Wire.Events;

namespace PiStation.PiRpc.Tests;

public sealed class RealPiSmokeTests
{
    [Fact]
    [Trait("Category", "RealPi")]
    public async Task InstalledPiCanCompleteOneRpcTurnWhenExplicitlyEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("PISTATION_RUN_REAL_PI"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        using var temporaryDirectory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var installation = await new PiLocator().LocateAsync(
            new PiLocatorOptions
            {
                ExplicitPiPath = Environment.GetEnvironmentVariable("PISTATION_PI_PATH"),
            },
            cancellation.Token);
        var projectDirectory = temporaryDirectory.CreateDirectory("project");
        var sessionDirectory = temporaryDirectory.CreateDirectory("sessions");

        Task<PiProcess> StartAsync() => PiProcessLauncher.StartAsync(
            new PiProcessLaunchOptions
            {
                Installation = installation,
                ProjectDirectory = projectDirectory,
                SessionDirectory = sessionDirectory,
                SessionId = "real-pi-smoke",
            },
            cancellation.Token);

        int firstEntryCount;
        await using (var first = await StartAsync())
        {
            await first.Connection.PromptAsync(
                "Reply with the exact words: Pi Station online",
                cancellation.Token);
            var events = await FakePiTestHost.ReadUntilSettledAsync(first.Connection, cancellation.Token);
            Assert.Contains(events, static @event => @event is PiAgentSettledEvent);
            var entries = await first.Connection.GetEntriesAsync(cancellationToken: cancellation.Token);
            Assert.NotEmpty(entries.Entries);
            firstEntryCount = entries.Entries.Count;
        }

        await using var resumed = await StartAsync();
        var resumedEntries = await resumed.Connection.GetEntriesAsync(cancellationToken: cancellation.Token);
        Assert.Equal(firstEntryCount, resumedEntries.Entries.Count);
    }
}
