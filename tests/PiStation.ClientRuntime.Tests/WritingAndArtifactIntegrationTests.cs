using Microsoft.AspNetCore.SignalR;
using PiStation.Host.Hosting;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed class WritingAndArtifactIntegrationTests
{
    [Fact]
    public async Task ClientReadsOutsideReportAndRoundTripsWritingPreferences()
    {
        using var directory = new ClientTestDirectory();
        var root = directory.CreateDirectory("project");
        var outside = Path.Combine(directory.CreateDirectory("reports"), "report.md");
        await File.WriteAllTextAsync(outside, "# Agent report");
        await using var host = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions());
        await using var client = new EnvironmentClient(new() { HubAddress = host.HubAddress, BearerCredential = host.BearerCredential });
        await client.ConnectAsync();
        var project = await client.AddProjectAsync(new(root));
        var artifact = await client.ReadArtifactFileAsync(new(new(project.ProjectId), outside));
        Assert.Equal("# Agent report", System.Text.Encoding.UTF8.GetString(artifact.Content));
        var initial = await client.GetSourceControlWritingSettingsAsync();
        var saved = await client.SaveSourceControlWritingSettingsAsync(initial with { Style = SourceControlWritingStyle.Custom, CustomInstructions = "Use short bullets" });
        Assert.Equal(saved, await client.GetSourceControlWritingSettingsAsync());
        var conflict = await Assert.ThrowsAsync<HubException>(() => client.SaveSourceControlWritingSettingsAsync(initial));
        Assert.Contains("Refresh Settings", conflict.Message);
        await Assert.ThrowsAsync<HubException>(() => client.ReadArtifactFileAsync(new(new(PiStation.Protocol.Identifiers.ProjectId.New()), outside)));
    }
}
