using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed class PiConfigurationStoreTests
{
    [Fact]
    public void StoreIgnoresOlderRevisionsButRefreshesCapabilitiesAtTheCurrentRevision()
    {
        var environmentId = EnvironmentId.New();
        var threadId = ThreadId.New();
        var store = new PiConfigurationStore();
        var first = CreateSnapshot(environmentId, threadId, 1, [CreateModel("fake-standard")]);
        var refreshed = CreateSnapshot(
            environmentId,
            threadId,
            1,
            [CreateModel("fake-standard"), CreateModel("fake-fast")]);
        var stale = CreateSnapshot(environmentId, threadId, 0, []);

        Assert.Equal(PiConfigurationApplyResult.Applied, store.Apply(first));
        Assert.Equal(PiConfigurationApplyResult.Applied, store.Apply(refreshed));
        Assert.Equal(PiConfigurationApplyResult.Ignored, store.Apply(stale));

        var current = Assert.IsType<ThreadPiConfigurationSnapshot>(store.GetCurrent(threadId));
        Assert.Equal(1, current.Configuration.Revision);
        Assert.Equal(2, current.Capabilities.Models.Count);
    }

    private static ThreadPiConfigurationSnapshot CreateSnapshot(
        EnvironmentId environmentId,
        ThreadId threadId,
        long revision,
        IReadOnlyList<PiModelCapability> models) => new(
        new ThreadPiConfiguration(
            environmentId,
            threadId,
            null,
            null,
            null,
            revision,
            DateTimeOffset.UtcNow),
        new PiConfigurationCapabilities(models, [PiThinkingLevel.Off], []),
        new PiModelSelection("fake", "fake-standard"),
        PiThinkingLevel.Off,
        null);

    private static PiModelCapability CreateModel(string modelId) =>
        new("fake", modelId, modelId, false);
}
