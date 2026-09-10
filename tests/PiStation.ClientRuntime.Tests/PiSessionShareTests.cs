namespace PiStation.ClientRuntime.Tests;

public sealed class PiSessionShareTests
{
    [Fact]
    public async Task UnlistedIsDefaultAndChangedReviewedContentCannotBePublished()
    {
        var path = Path.Combine(Path.GetTempPath(), "PiStation-share-test-" + Guid.NewGuid().ToString("N") + ".html");
        try
        {
            await File.WriteAllTextAsync(path, "<html>reviewed</html>");
            var prepared = await PiSessionShare.PrepareAsync(path);
            var start = PiSessionShare.CreateStartInfo(prepared, "literal `title` $(not-a-command)", false);
            Assert.False(start.UseShellExecute);
            Assert.DoesNotContain("--public", start.ArgumentList);
            Assert.Equal(path, start.ArgumentList.Last());
            Assert.Contains("--public", PiSessionShare.CreateStartInfo(prepared, "Title", true).ArgumentList);
            await File.WriteAllTextAsync(path, "<html>changed!</html>");
            await Assert.ThrowsAsync<InvalidDataException>(() => PiSessionShare.PublishAsync(prepared, "Title", false));
        }
        finally { File.Delete(path); }
    }
}
