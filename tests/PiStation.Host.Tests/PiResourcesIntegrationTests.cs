using Microsoft.AspNetCore.SignalR.Client;
using PiStation.Host.Hosting;
using PiStation.PiRpc.Discovery;
using PiStation.Protocol;
using PiStation.Protocol.Commands;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Streaming;

namespace PiStation.Host.Tests;

public sealed class PiResourcesIntegrationTests
{
    [Fact]
    public async Task ProcessFactoryPassesDedicatedPolicyIntoRuntimeReadback()
    {
        using var directory = new HostTestDirectory();
        var options = directory.CreateOptions("resource-management");
        options.PiInstallation = options.PiInstallation! with { PiVersion = new SemanticVersion(0, 85, 0) };
        options.LaunchConfiguration = new(Tools: new(PiToolSelectionMode.Allowlist, ["read", "powershell"], ["write"]));
        await using var host = await EmbeddedEnvironmentHost.StartAsync(options);
        var project = await host.Environment.AddProjectAsync(new(directory.CreateDirectory("project")));
        var thread = await host.Environment.CreateThreadAsync(new(project.ProjectId));
        var snapshot = await host.Environment.ManagePiResourcesAsync(new(thread.ThreadId));
        Assert.Equal(PiToolSelectionMode.Allowlist, snapshot.ToolInventory!.Selection!.Mode);
        Assert.Equal(["read", "powershell"], snapshot.ToolInventory.Selection.Allowed);
        Assert.Equal(["write"], snapshot.ToolInventory.Selection.Excluded);
    }

    [Fact]
    public async Task AuthenticatedHubManagesResourcesWithoutChangingDraftOrTranscript()
    {
        using var directory = new HostTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var host = await EmbeddedEnvironmentHost.StartAsync(directory.CreateOptions("resource-management"));
        var project = await host.Environment.AddProjectAsync(new AddProjectRequest(directory.CreateDirectory("project")));
        var thread = await host.Environment.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        var draft = await host.Environment.GetThreadDraftAsync(thread.ThreadId);
        await host.Environment.ExecuteThreadCommandAsync(new ExecuteThreadCommandRequest(
            ProtocolVersion.Current, thread.EnvironmentId, ClientId.New(), CommandId.New(), thread.ThreadId, null, null,
            new ThreadSaveDraftCommand(draft.DraftId, draft.Revision, "Keep this unsent draft.")), timeout.Token);
        draft = await host.Environment.GetThreadDraftAsync(thread.ThreadId);
        await using var connection = HostTestConnection.Create(host);
        await connection.StartAsync(timeout.Token);
        var snapshot = await connection.InvokeAsync<PiResourcesSnapshot>("ManagePiResources", new ManagePiResourcesRequest(thread.ThreadId), timeout.Token);
        var resource = Assert.Single(snapshot.Resources);
        Assert.True(resource.ConfirmedLoaded);
        Assert.True(snapshot.ToolInventory!.Tools.Single(tool => tool.Name == "read").Active);
        Assert.False(snapshot.ToolInventory.Tools.Single(tool => tool.Name == "powershell").Active);
        Assert.Equal(PiToolSelectionMode.PiDefault, snapshot.ToolInventory.Selection!.Mode);
        var saved = await connection.InvokeAsync<PiResourcesSnapshot>("ManagePiResources",
            new ManagePiResourcesRequest(thread.ThreadId, "toggle", resource.Id, false, resource.Revision), timeout.Token);
        Assert.False(Assert.Single(saved.Resources).Enabled);
        Assert.True(Assert.Single(saved.Resources).ConfirmedLoaded);
        var unchanged = await host.Environment.GetThreadDraftAsync(thread.ThreadId);
        Assert.Equal(draft.DraftId, unchanged.DraftId);
        Assert.Equal(draft.Revision, unchanged.Revision);
        Assert.Equal(draft.Text, unchanged.Text);
        Assert.Equal(draft.Attachments, unchanged.Attachments);
        Assert.Equal(draft.Context, unchanged.Context);
        await using var stream = host.Environment.SubscribeThreadAsync(thread.ThreadId, null, timeout.Token).GetAsyncEnumerator(timeout.Token);
        Assert.True(await stream.MoveNextAsync());
        var projection = Assert.IsType<ThreadSnapshotEnvelope>(stream.Current).Projection;
        Assert.Null(projection.CurrentTurnId);
        Assert.Empty(projection.Messages);
        Assert.Empty(projection.ExtensionUi?.Statuses ?? []);
        Assert.DoesNotContain((await host.Environment.GetComposerDiscoveryAsync(thread.ThreadId)).Commands,
            command => command.Name == "pistation-desktop-resources");
    }

    [Theory]
    [InlineData("login")]
    [InlineData("resources")]
    [InlineData("packages")]
    public void SetupUsesConfiguredRuntimeAndQuotesPowerShellArguments(string action)
    {
        var installation = new PiInstallation(PiInstallationKind.NodePackage, "C:/Pi's tools/node.exe",
            ["C:/Pi's tools/cli.js"], new(0, 84, 4), null, null, "test");
        var setup = PiSetupCommand.Create(installation, action);
        Assert.Contains("'C:/Pi''s tools/node.exe'", setup.Command);
        Assert.Contains("'C:/Pi''s tools/cli.js'", setup.Command);
        Assert.Contains("function global:pi", setup.Command);
        Assert.DoesNotContain("--credentials", setup.Command);
        Assert.Throws<ArgumentException>(() => PiSetupCommand.Create(installation, "login; bad-command"));
    }
}
