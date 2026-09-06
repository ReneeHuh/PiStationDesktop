using PiStation.PiRpc.Discovery;
using PiStation.PiRpc.Process;
using PiStation.PiRpc.Transport;

namespace PiStation.PiRpc.Tests;

public sealed class RealPiAuthenticatedSetupFactAttribute : FactAttribute
{
    public RealPiAuthenticatedSetupFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PISTATION_RUN_AUTHENTICATED_SETUP") != "1")
            Skip = "Opt in with PISTATION_RUN_AUTHENTICATED_SETUP=1 and PISTATION_TEST_PROVIDER/MODEL to use Pi's configured credentials.";
    }
}

public sealed class RealPiAuthenticatedSetupTests
{
    [RealPiAuthenticatedSetupFact]
    [Trait("Category", "RealPi")]
    public async Task ConfiguredProviderInvokesASkillAndResumesAfterRestart()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var provider = Environment.GetEnvironmentVariable("PISTATION_TEST_PROVIDER") ?? throw new InvalidOperationException("Choose a test provider.");
        var model = Environment.GetEnvironmentVariable("PISTATION_TEST_MODEL") ?? throw new InvalidOperationException("Choose a test model.");
        var skill = directory.GetPath("SKILL.md");
        await File.WriteAllTextAsync(skill, "---\nname: authentication-smoke\ndescription: Minimal integration check\n---\nReply with exactly PISTATION_AUTH_OK. Do not call tools.", timeout.Token);
        var options = new PiProcessLaunchOptions
        {
            Installation = await new PiLocator().LocateAsync(new PiLocatorOptions { ExplicitPiPath = Environment.GetEnvironmentVariable("PISTATION_PI_PATH") }, timeout.Token),
            ProjectDirectory = directory.CreateDirectory("project"), SessionDirectory = directory.CreateDirectory("sessions"),
            SessionId = Guid.NewGuid().ToString(),
            AdditionalArguments = ["--extension", Path.Combine(AppContext.BaseDirectory, "Fixtures", "pistation-resources.ts"),
                "--no-skills", "--no-prompt-templates", "--no-context-files", "--no-tools", "--thinking", "off",
                "--skill", skill, "--provider", provider, "--model", model],
        };
        await using (var process = await PiProcessLauncher.StartAsync(options, timeout.Token))
        {
            var inventory = await process.Connection.ManageAsync(new() { ["action"] = "inspect" }, timeout.Token);
            Assert.Contains(inventory.GetProperty("providers").EnumerateArray(),
                item => item.GetProperty("providerId").GetString() == provider && item.GetProperty("credentialConfigured").GetBoolean());
            await process.Connection.PromptAsync("Use $skill:authentication-smoke for this integration check.", timeout.Token);
            await FakePiTestHost.ReadUntilSettledAsync(process.Connection, timeout.Token);
            var entries = await process.Connection.GetEntriesAsync(cancellationToken: timeout.Token);
            Assert.Contains(entries.Entries, entry => entry.GetRawText().Contains("PISTATION_AUTH_OK", StringComparison.Ordinal) &&
                entry.GetRawText().Contains("\"assistant\"", StringComparison.Ordinal));
        }
        await using var resumed = await PiProcessLauncher.StartAsync(options, timeout.Token);
        var restored = await resumed.Connection.GetEntriesAsync(cancellationToken: timeout.Token);
        Assert.Contains(restored.Entries, entry => entry.GetRawText().Contains("\"assistant\"", StringComparison.Ordinal));
        await resumed.Connection.PromptAsync("Use $skill:authentication-smoke once more after restart.", timeout.Token);
        await FakePiTestHost.ReadUntilSettledAsync(resumed.Connection, timeout.Token);
        var completed = await resumed.Connection.GetEntriesAsync(cancellationToken: timeout.Token);
        Assert.True(completed.Entries.Count > restored.Entries.Count);
        Assert.Contains("PISTATION_AUTH_OK", completed.Entries.Last(entry =>
            entry.GetRawText().Contains("\"assistant\"", StringComparison.Ordinal)).GetRawText());
    }
}
