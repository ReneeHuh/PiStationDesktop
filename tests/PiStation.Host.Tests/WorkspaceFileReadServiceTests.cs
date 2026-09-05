using PiStation.Host.Errors;
using PiStation.Host.Files;
using PiStation.Host.Persistence;
using PiStation.Host.Projects;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class WorkspaceFileReadServiceTests
{
    [Fact]
    public async Task ReadReturnsBoundedUtf8TextFromTheProject()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var projectRoot = temporaryDirectory.CreateDirectory("project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "src", "Program.cs"), "hello world");
        var database = new HostDatabase(options);
        await database.InitializeAsync();
        var project = await new ProjectService(database).AddAsync(new AddProjectRequest(projectRoot));
        var service = new WorkspaceFileReadService(database);

        var result = await service.ReadAsync(new ReadProjectFileRequest(
            project.ProjectId,
            "src/Program.cs",
            MaximumBytes: 5));

        Assert.Equal("src/Program.cs", result.RelativePath);
        Assert.Equal("hello", result.Content);
        Assert.Equal(11, result.ByteLength);
        Assert.True(result.IsTruncated);
        Assert.False(result.IsBinary);
    }

    [Fact]
    public async Task ReadClassifiesBinaryFilesWithoutReturningTheirContent()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var projectRoot = temporaryDirectory.CreateDirectory("project");
        await File.WriteAllBytesAsync(Path.Combine(projectRoot, "image.bin"), [1, 0, 2, 3]);
        var database = new HostDatabase(options);
        await database.InitializeAsync();
        var project = await new ProjectService(database).AddAsync(new AddProjectRequest(projectRoot));
        var service = new WorkspaceFileReadService(database);

        var result = await service.ReadAsync(new ReadProjectFileRequest(project.ProjectId, "image.bin"));

        Assert.True(result.IsBinary);
        Assert.Empty(result.Content);
        Assert.Equal(4, result.ByteLength);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("src/../../outside.txt")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("/etc/passwd")]
    public async Task ReadRejectsPathsOutsideTheProject(string relativePath)
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var projectRoot = temporaryDirectory.CreateDirectory("project");
        var database = new HostDatabase(options);
        await database.InitializeAsync();
        var project = await new ProjectService(database).AddAsync(new AddProjectRequest(projectRoot));
        var service = new WorkspaceFileReadService(database);

        var exception = await Assert.ThrowsAsync<HostOperationException>(() => service.ReadAsync(
            new ReadProjectFileRequest(project.ProjectId, relativePath)));

        Assert.Equal(ProtocolErrorCodes.FileReadInvalid, exception.Code);
    }

    [Fact]
    public async Task SaveUsesRevisionTokensAndRejectsStaleEditors()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var projectRoot = temporaryDirectory.CreateDirectory("project");
        var filePath = Path.Combine(projectRoot, "README.md");
        await File.WriteAllTextAsync(filePath, "before");
        var database = new HostDatabase(options);
        await database.InitializeAsync();
        var project = await new ProjectService(database).AddAsync(new AddProjectRequest(projectRoot));
        var service = new WorkspaceFileReadService(database);
        var opened = await service.ReadAsync(new ReadProjectFileRequest(project.ProjectId, "README.md"));

        var saved = await service.SaveAsync(new SaveProjectFileRequest(
            project.ProjectId,
            "README.md",
            "after",
            opened.Revision));
        var conflict = await Assert.ThrowsAsync<HostOperationException>(() => service.SaveAsync(
            new SaveProjectFileRequest(project.ProjectId, "README.md", "stale", opened.Revision)));

        Assert.Equal("after", await File.ReadAllTextAsync(filePath));
        Assert.NotEqual(opened.Revision, saved.Revision);
        Assert.Equal(ProtocolErrorCodes.FileWriteConflict, conflict.Code);
    }

    [Fact]
    public async Task AssetReadReturnsImageBytesAndMediaType()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var projectRoot = temporaryDirectory.CreateDirectory("project");
        var bytes = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        await File.WriteAllBytesAsync(Path.Combine(projectRoot, "icon.png"), bytes);
        var database = new HostDatabase(options);
        await database.InitializeAsync();
        var project = await new ProjectService(database).AddAsync(new AddProjectRequest(projectRoot));
        var service = new WorkspaceFileReadService(database);

        var result = await service.ReadAssetAsync(new ReadProjectFileAssetRequest(
            project.ProjectId,
            "icon.png"));

        Assert.Equal(bytes, result.Content);
        Assert.Equal("image/png", result.MediaType);
        Assert.NotEmpty(result.Revision);
    }
}
