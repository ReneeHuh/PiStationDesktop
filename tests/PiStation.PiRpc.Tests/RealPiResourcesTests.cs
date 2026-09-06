using System.Text.Json;
using System.Text.Json.Nodes;
using PiStation.PiRpc.Diagnostics;
using PiStation.PiRpc.Discovery;
using PiStation.PiRpc.Process;

namespace PiStation.PiRpc.Tests;

public sealed class RealPiResourcesTests
{
    [RealPiOfflineFact]
    [Trait("Category", "RealPiOffline")]
    public async Task ResourcesTrustAndCustomModelSettingsSurviveRuntimeRestart()
    {
        using var directory = new TemporaryDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var project = directory.CreateDirectory("project");
        var agent = directory.CreateDirectory("agent");
        var skillDirectory = directory.CreateDirectory("agent/skills/managed");
        await File.WriteAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"), "---\nname: managed\ndescription: Test skill\n---\nMANAGED_SKILL_BODY", timeout.Token);
        var projectSkills = directory.CreateDirectory("project/.pi/skills/private");
        await File.WriteAllTextAsync(Path.Combine(projectSkills, "SKILL.md"), "---\nname: private\ndescription: Private skill\n---\nPRIVATE_SKILL_BODY", timeout.Token);
        var package = directory.CreateDirectory("fixture-package");
        var packageSkill = directory.CreateDirectory("fixture-package/skills/packaged");
        await File.WriteAllTextAsync(Path.Combine(packageSkill, "SKILL.md"), "---\nname: packaged\ndescription: Package skill\n---\nPACKAGE_SKILL_BODY", timeout.Token);
        await File.WriteAllTextAsync(Path.Combine(package, "package.json"), "{\"name\":\"pistation-test-package\",\"version\":\"1.0.0\",\"pi\":{\"skills\":[\"./skills\"]}}", timeout.Token);
        var settingsPath = Path.Combine(agent, "settings.json");
        await File.WriteAllTextAsync(settingsPath, new JsonObject { ["theme"] = "light", ["packages"] = new JsonArray(package) }.ToJsonString(), timeout.Token);
        var modelsPath = Path.Combine(agent, "models.json");
        await File.WriteAllTextAsync(modelsPath, "// Existing Pi configuration\n{\"providers\":{\"pistation-local\":{\"apiKey\":\"preserved-fixture-key\",\"baseUrl\":\"http://127.0.0.1:18080/v1\",\"api\":\"openai-completions\",\"models\":[{\"id\":\"existing-model\",\"contextWindow\":4096}]}}}", timeout.Token);
        var options = new PiProcessLaunchOptions
        {
            Installation = await new PiLocator().LocateAsync(new PiLocatorOptions { ExplicitPiPath = Environment.GetEnvironmentVariable("PISTATION_PI_PATH") }, timeout.Token),
            ProjectDirectory = project, SessionDirectory = directory.CreateDirectory("sessions"), SessionId = Guid.NewGuid().ToString(),
            AdditionalArguments = ["--extension", Path.Combine(AppContext.BaseDirectory, "Fixtures", "pistation-resources.ts"),
                "--extension", Path.Combine(AppContext.BaseDirectory, "Fixtures", "offline-provider.ts"), "--provider", "pistation-offline", "--model", "deterministic"],
            EnvironmentVariables = new Dictionary<string, string?> { ["PI_CODING_AGENT_DIR"] = agent, ["PISTATION_TEST_TRACE"] = directory.GetPath("trace.jsonl") },
        };
        await using (var process = await PiProcessLauncher.StartAsync(options, timeout.Token))
        {
            var snapshot = await process.Connection.ManageAsync(new() { ["action"] = "inspect" }, timeout.Token);
            Assert.False(snapshot.GetProperty("projectTrusted").GetBoolean());
            var skill = snapshot.GetProperty("resources").EnumerateArray().Single(resource => resource.GetProperty("name").GetString() == "managed");
            Assert.True(skill.GetProperty("confirmedLoaded").GetBoolean());
            Assert.DoesNotContain(snapshot.GetProperty("resources").EnumerateArray(), item => item.GetProperty("name").GetString() == "private");
            Assert.Contains(snapshot.GetProperty("providers").EnumerateArray(), item => item.GetProperty("providerId").GetString() == "pistation-offline" && item.GetProperty("credentialConfigured").GetBoolean());
            Assert.DoesNotContain("offline-fixture", snapshot.GetRawText());
            Assert.DoesNotContain("preserved-fixture-key", snapshot.GetRawText());
            var toggle = new JsonObject { ["action"] = "toggle", ["resourceId"] = skill.GetProperty("id").GetString(),
                ["enabled"] = false, ["revision"] = skill.GetProperty("revision").GetString() };
            await process.Connection.ManageAsync(toggle, timeout.Token);
            await Assert.ThrowsAsync<PiRpcCommandException>(() => process.Connection.ManageAsync(toggle, timeout.Token));
            var packageInventory = await process.Connection.ManageAsync(new() { ["action"] = "inspect" }, timeout.Token);
            var packaged = packageInventory.GetProperty("resources").EnumerateArray().Single(resource => resource.GetProperty("name").GetString() == "packaged");
            await process.Connection.ManageAsync(new() { ["action"] = "toggle", ["resourceId"] = packaged.GetProperty("id").GetString(),
                ["enabled"] = false, ["revision"] = packaged.GetProperty("revision").GetString() }, timeout.Token);
            await process.Connection.ManageAsync(new() { ["action"] = "trust", ["enabled"] = true }, timeout.Token);
            var modelRequest = new JsonObject
            {
                ["action"] = "saveModel", ["revision"] = snapshot.GetProperty("modelsRevision").GetString(),
                ["model"] = new JsonObject { ["providerId"] = "pistation-local", ["modelId"] = "test-model", ["displayName"] = "Local Test",
                    ["baseUrl"] = "http://127.0.0.1:18080/v1", ["api"] = "openai-completions" },
            };
            var originalModels = await File.ReadAllTextAsync(modelsPath, timeout.Token);
            modelRequest["model"]!["apiKeyEnvironmentVariable"] = "!echo not-a-variable";
            await Assert.ThrowsAsync<PiRpcCommandException>(() => process.Connection.ManageAsync(modelRequest, timeout.Token));
            Assert.Equal(originalModels, await File.ReadAllTextAsync(modelsPath, timeout.Token));
            modelRequest["model"]!.AsObject().Remove("apiKeyEnvironmentVariable");
            await process.Connection.ManageAsync(modelRequest, timeout.Token);
            await Assert.ThrowsAsync<PiRpcCommandException>(() => process.Connection.ManageAsync(modelRequest, timeout.Token));
            var savedModels = JsonNode.Parse(await File.ReadAllTextAsync(modelsPath, timeout.Token))!;
            Assert.Equal("preserved-fixture-key", savedModels["providers"]!["pistation-local"]!["apiKey"]!.GetValue<string>());
            Assert.Equal(4096, savedModels["providers"]!["pistation-local"]!["models"]![0]!["contextWindow"]!.GetValue<int>());
            var validModels = await File.ReadAllTextAsync(modelsPath, timeout.Token);
            await File.WriteAllTextAsync(modelsPath, "{invalid", timeout.Token);
            var invalid = await process.Connection.ManageAsync(new() { ["action"] = "inspect" }, timeout.Token);
            Assert.NotEmpty(invalid.GetProperty("diagnostics").EnumerateArray());
            await Assert.ThrowsAsync<PiRpcCommandException>(() => process.Connection.ManageAsync(modelRequest, timeout.Token));
            Assert.Equal("{invalid", await File.ReadAllTextAsync(modelsPath, timeout.Token));
            await File.WriteAllTextAsync(modelsPath, validModels, timeout.Token);
            Assert.DoesNotContain((await process.Connection.GetEntriesAsync(cancellationToken: timeout.Token)).Entries,
                entry => entry.TryGetProperty("type", out var kind) && kind.GetString() == "message");
        }
        await using var resumed = await PiProcessLauncher.StartAsync(options, timeout.Token);
        var refreshed = await resumed.Connection.ManageAsync(new() { ["action"] = "inspect" }, timeout.Token);
        Assert.True(refreshed.GetProperty("projectTrusted").GetBoolean());
        var disabled = refreshed.GetProperty("resources").EnumerateArray().Single(resource => resource.GetProperty("name").GetString() == "managed");
        Assert.False(disabled.GetProperty("enabled").GetBoolean());
        Assert.False(disabled.GetProperty("confirmedLoaded").GetBoolean());
        Assert.Contains(refreshed.GetProperty("resources").EnumerateArray(), item => item.GetProperty("name").GetString() == "packaged" &&
            !item.GetProperty("enabled").GetBoolean() && !item.GetProperty("confirmedLoaded").GetBoolean());
        Assert.Contains(refreshed.GetProperty("resources").EnumerateArray(), item => item.GetProperty("name").GetString() == "private" && item.GetProperty("confirmedLoaded").GetBoolean());
        Assert.Contains(refreshed.GetProperty("providers").EnumerateArray(), item => item.GetProperty("providerId").GetString() == "pistation-local" && item.GetProperty("credentialConfigured").GetBoolean());
        await resumed.Connection.ManageAsync(new() { ["action"] = "toggle", ["resourceId"] = disabled.GetProperty("id").GetString(),
            ["enabled"] = true, ["revision"] = disabled.GetProperty("revision").GetString() }, timeout.Token);
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(agent, "settings.json"), timeout.Token))!;
        Assert.Contains(settings["skills"]!.AsArray(), value => value!.GetValue<string>().StartsWith('+'));
        Assert.Equal("light", settings["theme"]!.GetValue<string>());
        Assert.Equal(package, settings["packages"]![0]!["source"]!.GetValue<string>());
        Assert.Contains(settings["packages"]![0]!["skills"]!.AsArray(), value => value!.GetValue<string>().StartsWith('-'));
    }
}
