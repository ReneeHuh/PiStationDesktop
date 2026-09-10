namespace PiStation.ClientRuntime.Tests;

public sealed class TemporarySessionStorageTests
{
    [Fact]
    public async Task CloseRemovesDraftAndCachesWhileCrashCleanupPreservesALiveOwner()
    {
        var parent = Path.Combine(Path.GetTempPath(), "PiStation-temporary-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var storage = TemporarySessionStorage.Create(parent);
            await File.WriteAllTextAsync(Path.Combine(storage.Root, "draft.json"), "private draft");
            Assert.Contains(storage.Root, TemporarySessionStorage.CleanAbandoned(parent));
            Assert.True(File.Exists(Path.Combine(storage.Root, "draft.json")));
            var abandoned = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(abandoned);
            await File.WriteAllTextAsync(Path.Combine(abandoned, "draft.json"), "crashed draft");
            TemporarySessionStorage.CleanAbandoned(parent);
            Assert.False(Directory.Exists(abandoned));
            await storage.DisposeAsync();
            Assert.False(Directory.Exists(storage.Root));
        }
        finally { if (Directory.Exists(parent)) Directory.Delete(parent, true); }
    }
}
