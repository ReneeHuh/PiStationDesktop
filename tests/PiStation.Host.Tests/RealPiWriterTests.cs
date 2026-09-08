using System.Text.Json;
using PiStation.Host.SourceControl;
using PiStation.PiRpc.Discovery;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class RealPiWriterFactAttribute : FactAttribute
{
    public RealPiWriterFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PISTATION_RUN_REAL_PI_WRITER") != "1")
            Skip = "Opt in with PISTATION_RUN_REAL_PI_WRITER=1 and an isolated PI_CODING_AGENT_DIR under the temporary directory.";
    }
}

public sealed class RealPiWriterTests
{
    [RealPiWriterFact]
    public async Task InstalledPiGeneratesStructuredTextWithoutToolsSessionsOrSettingsChanges()
    {
        var agentRoot = Path.GetFullPath(Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") ?? throw new InvalidOperationException("An isolated Pi directory is required."));
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "PiStation.WriterSmoke-"), agentRoot, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(agentRoot);
        var settingsPath = Path.Combine(agentRoot, "settings.json");
        const string savedSettings = "{\"defaultProvider\":\"sentinel\",\"defaultModel\":\"unchanged\",\"defaultThinkingLevel\":\"off\"}";
        await File.WriteAllTextAsync(settingsPath, savedSettings);
        using var directory = new HostTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var fixture = directory.GetPath("writer-provider.ts");
        var trace = directory.GetPath("writer-context.json");
        await File.WriteAllTextAsync(fixture, """
            import { writeFileSync } from "node:fs";
            import { createAssistantMessageEventStream } from "@earendil-works/pi-ai/compat";
            export default function(pi: any) {
              pi.registerProvider("pistation-writer-offline", {
                baseUrl: "http://127.0.0.1:1", apiKey: "offline-fixture", api: "pistation-writer-api",
                models: [{id:"deterministic",name:"Offline writer",reasoning:false,input:["text"],
                  cost:{input:0,output:0,cacheRead:0,cacheWrite:0},contextWindow:100000,maxTokens:1000}],
                streamSimple(model: any, context: any) {
                  writeFileSync(TRACE_PATH, JSON.stringify(context));
                  const stream = createAssistantMessageEventStream();
                  const message: any = {role:"assistant",api:model.api,provider:model.provider,model:model.id,
                    content:[{type:"text",text:JSON.stringify({title:"Fix writer generation",body:"Describe the actual change"})}],
                    usage:{input:10,output:5,cacheRead:0,cacheWrite:0,totalTokens:15,cost:{input:0,output:0,cacheRead:0,cacheWrite:0,total:0}},
                    stopReason:"stop",timestamp:Date.now()};
                  queueMicrotask(()=> {stream.push({type:"start",partial:message});stream.push({type:"done",reason:"stop",message});stream.end();});
                  return stream;
                }
              });
            }
            """.Replace("TRACE_PATH", JsonSerializer.Serialize(trace), StringComparison.Ordinal), timeout.Token);
        var installation = await new PiLocator().LocateAsync(new PiLocatorOptions { ExplicitPiPath = Environment.GetEnvironmentVariable("PISTATION_PI_PATH") }, timeout.Token);
        var options = directory.CreateOptions() with { PiInstallation = installation, AdditionalPiArguments = ["--extension", fixture] };
        var writer = new PiSourceControlTextGenerator(options);
        var result = await writer.GenerateAsync(directory.CreateDirectory("project"), "Describe the supplied diff.", new PiModelSelection("pistation-writer-offline", "deterministic"), timeout.Token);
        Assert.Equal("Fix writer generation", result.Title);
        Assert.Equal(savedSettings, await File.ReadAllTextAsync(settingsPath, timeout.Token));
        Assert.Empty(Directory.EnumerateFiles(options.CanonicalDataRoot, "*.jsonl", SearchOption.AllDirectories));
        using var context = JsonDocument.Parse(await File.ReadAllTextAsync(trace, timeout.Token));
        Assert.True(!context.RootElement.TryGetProperty("tools", out var tools) || tools.GetArrayLength() == 0);
        var messages = context.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Single(messages);
        Assert.Contains("Describe the supplied diff", messages[0].GetRawText());
    }
}
