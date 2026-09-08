namespace PiStation.ClientRuntime.Tests;

public sealed class ArtifactLinkTests
{
    [Theory]
    [InlineData("#L12-L15", 12)]
    [InlineData(":42:3", 42)]
    [InlineData("", null)]
    public void ResolvesAbsoluteReportsAndLineReferences(string suffix, int? line)
    {
        var path = Path.Combine(Path.GetTempPath(), "pi-report.md");
        var result = ArtifactLink.Parse(path + suffix);
        Assert.Equal(path, result?.AbsolutePath);
        Assert.Equal(line, result?.Line);
    }

    [Fact]
    public void FileUriDecodesEscapesOnceAndPreservesEncodedLineLikeFilename()
    {
        var path = Path.Combine(Path.GetTempPath(), "my report#L12.md");
        var uri = new Uri(path).AbsoluteUri;
        Assert.Equal(path, ArtifactLink.Parse(uri + "#L7")?.AbsolutePath);
        Assert.Equal(7, ArtifactLink.Parse(uri + "#L7")?.Line);
        var literal = Path.Combine(Path.GetTempPath(), "percent%20.txt");
        Assert.Equal(literal, ArtifactLink.Parse(new Uri(literal).AbsoluteUri)?.AbsolutePath);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("relative.txt")]
    [InlineData("https://example.com/report.pdf")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file://another-machine/share/report.txt")]
    [InlineData("\\\\.\\pipe\\anything")]
    public void RejectsRelativePathsWebLinksAndNetworkOrDevicePaths(string value) => Assert.Null(ArtifactLink.Parse(value));
}
