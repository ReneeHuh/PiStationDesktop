namespace PiStation.ClientRuntime.Tests;

public sealed class WorkspaceLinkTests
{
    [Theory]
    [InlineData("src/app.cs#L12-L15", "src/app.cs", 12)]
    [InlineData("src/app.cs:42:3", "src/app.cs", 42)]
    [InlineData("src/my%20file.cs", "src/my file.cs", null)]
    public void ResolvesWorkspacePathsAndLines(string input, string path, int? line)
    {
        var result = WorkspaceLink.Parse(input, Path.GetFullPath("workspace"));
        Assert.Equal(path, result?.RelativePath);
        Assert.Equal(line, result?.Line);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("%2e%2e/outside.txt")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://example.com/file")]
    public void RejectsOutsideFilesAndOtherSchemes(string input) =>
        Assert.Null(WorkspaceLink.Parse(input, Path.GetFullPath("workspace")));
}
