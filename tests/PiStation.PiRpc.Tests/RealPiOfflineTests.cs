using System.Text.Json;
using PiStation.PiRpc.Discovery;
using PiStation.PiRpc.Process;
using PiStation.PiRpc.Transport;
using PiStation.PiRpc.Wire.Events;

namespace PiStation.PiRpc.Tests;

public sealed class RealPiOfflineFactAttribute : FactAttribute
{
    public RealPiOfflineFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PISTATION_RUN_REAL_PI_OFFLINE") != "1")
            Skip = "Set PISTATION_RUN_REAL_PI_OFFLINE=1 to test an installed Pi with an isolated offline provider.";
    }
}

public sealed class RealPiOfflineTests
{
    [RealPiOfflineFact]
    [Trait("Category", "RealPiOffline")]
    public async Task InstalledPiInvokesSkillsTemplatesToolsAndExtensionUiThenResumes()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var installation = await new PiLocator().LocateAsync(new PiLocatorOptions
        {
            ExplicitPiPath = Environment.GetEnvironmentVariable("PISTATION_PI_PATH"),
        }, timeout.Token);
        var project = directory.CreateDirectory("project");
        var agent = directory.CreateDirectory("agent");
        var sessions = directory.CreateDirectory("sessions");
        var skillDirectory = directory.CreateDirectory("probe");
        var skill = Path.Combine(skillDirectory, "SKILL.md");
        await File.WriteAllTextAsync(skill, "---\nname: probe\ndescription: Offline probe\n---\nPISTATION_SKILL_BODY: inspect the selected changes.", timeout.Token);
        var template = directory.GetPath("probe-template.md");
        await File.WriteAllTextAsync(template, "---\ndescription: Offline template\n---\nPISTATION_TEMPLATE_BODY $1", timeout.Token);
        var trace = directory.GetPath("model-input.jsonl");
        var options = new PiProcessLaunchOptions
        {
            Installation = installation, ProjectDirectory = project, SessionDirectory = sessions,
            SessionId = Guid.NewGuid().ToString(),
            AdditionalArguments = ["--extension", Path.Combine(AppContext.BaseDirectory, "Fixtures", "offline-provider.ts"),
                "--skill", skill, "--prompt-template", template, "--provider", "pistation-offline", "--model", "deterministic"],
            EnvironmentVariables = new Dictionary<string, string?> { ["PI_CODING_AGENT_DIR"] = agent, ["PISTATION_TEST_TRACE"] = trace },
            ConnectionOptions = new PiRpcConnectionOptions { DefaultCommandTimeout = TimeSpan.FromSeconds(30) },
        };
        int entryCount;
        await using (var process = await PiProcessLauncher.StartAsync(options, timeout.Token))
        {
            var commands = await process.Connection.GetCommandsAsync(timeout.Token);
            Assert.Contains(commands, command => command.Name == "pistation-probe" && command.Source == "extension");
            Assert.Contains(commands, command => command.Name == "skill:probe" && command.Path == skill);
            await process.Connection.PromptAsync("Please use $skill:probe and call probe tool.", timeout.Token);
            var turn = await FakePiTestHost.ReadUntilSettledAsync(process.Connection, timeout.Token);
            Assert.Contains(turn, item => item is PiToolExecutionCompletedEvent { ToolName: "pistation_probe", IsError: false });
            Assert.Contains("PISTATION_SKILL_BODY", await File.ReadAllTextAsync(trace, timeout.Token));
            await process.Connection.PromptAsync("/probe-template ARGUMENT_SENTINEL", timeout.Token);
            await FakePiTestHost.ReadUntilSettledAsync(process.Connection, timeout.Token);
            Assert.Contains("PISTATION_TEMPLATE_BODY ARGUMENT_SENTINEL", await File.ReadAllTextAsync(trace, timeout.Token));
            await process.Connection.PromptAsync("/pistation-probe", timeout.Token);
            await process.Connection.ObserveHandledPromptAsync("probe-command", timeout.Token);
            var updates = new List<PiExtensionUiUpdateEvent>();
            await foreach (var item in process.Connection.ReadEventsAsync(timeout.Token))
            {
                if (item is PiExtensionUiUpdateEvent update) updates.Add(update);
                if (item is PiIdlePromptCompletedEvent) break;
            }
            Assert.Contains(updates, update => update.Method == "notify" && update.Text == "PROBE_NOTIFICATION");
            Assert.Contains(updates, update => update.Method == "setStatus" && update.Key == "probe");
            Assert.Contains(updates, update => update.Method == "setWidget" && update.Placement == "belowEditor");
            Assert.Contains(updates, update => update.Method == "setTitle" && update.Text == "PROBE_TITLE");
            Assert.Contains(updates, update => update.Method == "set_editor_text" && update.Text == "PROBE_DRAFT");
            await process.Connection.PromptAsync("/pistation-clear", timeout.Token);
            await process.Connection.ObserveHandledPromptAsync("clear-command", timeout.Token);
            var removals = new List<PiExtensionUiUpdateEvent>();
            await foreach (var item in process.Connection.ReadEventsAsync(timeout.Token))
            {
                if (item is PiExtensionUiUpdateEvent update) removals.Add(update);
                if (item is PiIdlePromptCompletedEvent) break;
            }
            Assert.Contains(removals, update => update.Method == "setStatus" && update.Text is null);
            Assert.Contains(removals, update => update.Method == "setWidget" && update.Lines is null);
            entryCount = (await process.Connection.GetEntriesAsync(cancellationToken: timeout.Token)).Entries.Count;
            Assert.True(entryCount >= 4);
        }
        await using var resumed = await PiProcessLauncher.StartAsync(options, timeout.Token);
        Assert.Equal(entryCount, (await resumed.Connection.GetEntriesAsync(cancellationToken: timeout.Token)).Entries.Count);
        await resumed.Connection.PromptAsync("Resume this session using $skill:probe.", timeout.Token);
        await FakePiTestHost.ReadUntilSettledAsync(resumed.Connection, timeout.Token);
        Assert.True((await resumed.Connection.GetEntriesAsync(cancellationToken: timeout.Token)).Entries.Count > entryCount);
        var installedDirectory = Path.Combine(agent, "extensions");
        Directory.CreateDirectory(installedDirectory);
        await File.WriteAllTextAsync(Path.Combine(installedDirectory, "discovered.ts"),
            """export default function(pi) { pi.registerCommand("discovered-probe", { description: "Discovered resource", handler: async () => {} }); }""", timeout.Token);
        foreach (var discover in new[] { false, true, false })
        {
            await using var configured = await PiProcessLauncher.StartAsync(options with
            {
                SessionId = Guid.NewGuid().ToString(), DiscoverExtensions = discover,
            }, timeout.Token);
            var commands = await configured.Connection.GetCommandsAsync(timeout.Token);
            Assert.Equal(discover, commands.Any(command => command.Name == "discovered-probe"));
            Assert.Contains(commands, command => command.Name == "pistation-probe");
        }
    }
}
