using PiStation.Host.Errors;
using PiStation.Host.Files;
using PiStation.Host.Persistence;
using PiStation.Host.Projects;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class WorkspaceFileSearchServiceTests
{
    [Fact]
    public async Task SearchReturnsRankedContainedRelativePathsAndSkipsGeneratedTrees()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var projectRoot = temporaryDirectory.CreateDirectory("project");
        var sourceDirectory = Directory.CreateDirectory(Path.Combine(projectRoot, "src"));
        var gitDirectory = Directory.CreateDirectory(Path.Combine(projectRoot, ".git"));
        var vendorDirectory = Directory.CreateDirectory(Path.Combine(projectRoot, "node_modules"));
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory.FullName, "Program.cs"), "class Program;");
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory.FullName, "Program.cs.bak"), "backup");
        await File.WriteAllTextAsync(Path.Combine(gitDirectory.FullName, "Program.cs"), "git data");
        await File.WriteAllTextAsync(Path.Combine(vendorDirectory.FullName, "Program.cs"), "vendor data");
        var outsideRoot = temporaryDirectory.CreateDirectory("outside");
        await File.WriteAllTextAsync(Path.Combine(outsideRoot, "Program.cs"), "outside");
        TryCreateDirectoryLink(Path.Combine(projectRoot, "outside-link"), outsideRoot);
        var database = new HostDatabase(options);
        await database.InitializeAsync();
        var projects = new ProjectService(database);
        var project = await projects.AddAsync(new AddProjectRequest(projectRoot));
        var service = new WorkspaceFileSearchService(database, options);

        var result = await service.SearchAsync(new SearchProjectFilesRequest(
            project.ProjectId,
            "  Program.cs  ",
            10));

        Assert.Equal("Program.cs", result.Query);
        Assert.Equal("src/Program.cs", result.Matches[0].RelativePath);
        Assert.Equal("src/Program.cs.bak", result.Matches[1].RelativePath);
        Assert.All(result.Matches, static match =>
        {
            Assert.False(Path.IsPathRooted(match.RelativePath));
            Assert.DoesNotContain("..", match.RelativePath, StringComparison.Ordinal);
            Assert.DoesNotContain('\\', match.RelativePath);
        });
        Assert.DoesNotContain(result.Matches, static match =>
            match.RelativePath.Contains(".git", StringComparison.OrdinalIgnoreCase) ||
            match.RelativePath.Contains("node_modules", StringComparison.OrdinalIgnoreCase) ||
            match.RelativePath.Contains("outside-link", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SearchReportsResultAndScanTruncation()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions() with { MaximumFileSearchScannedFiles = 2 };
        var projectRoot = temporaryDirectory.CreateDirectory("project");
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "a.txt"), "a");
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "b.txt"), "b");
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "c.txt"), "c");
        var database = new HostDatabase(options);
        await database.InitializeAsync();
        var project = await new ProjectService(database).AddAsync(new AddProjectRequest(projectRoot));
        var service = new WorkspaceFileSearchService(database, options);

        var result = await service.SearchAsync(new SearchProjectFilesRequest(project.ProjectId, string.Empty, 1));

        Assert.Single(result.Matches);
        Assert.True(result.IsTruncated);
    }

    [Fact]
    public async Task SearchRejectsUnknownProjectsAndInvalidLimits()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var database = new HostDatabase(options);
        await database.InitializeAsync();
        var service = new WorkspaceFileSearchService(database, options);

        var missing = await Assert.ThrowsAsync<HostOperationException>(() => service.SearchAsync(
            new SearchProjectFilesRequest(ProjectId.New(), "file")));
        var invalid = await Assert.ThrowsAsync<HostOperationException>(() => service.SearchAsync(
            new SearchProjectFilesRequest(ProjectId.New(), "file", FileSearchDefaults.MaximumResults + 1)));

        Assert.Equal(ProtocolErrorCodes.ProjectNotFound, missing.Code);
        Assert.Equal(ProtocolErrorCodes.FileSearchInvalid, invalid.Code);
    }

    [Fact]
    public async Task ListBuildsAContainedDirectorySurfaceAndContentSearchReportsLines()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var projectRoot = temporaryDirectory.CreateDirectory("project");
        Directory.CreateDirectory(Path.Combine(projectRoot, "src", "nested"));
        Directory.CreateDirectory(Path.Combine(projectRoot, "obj"));
        await File.WriteAllTextAsync(
            Path.Combine(projectRoot, "src", "nested", "Program.cs"),
            "first line\nclass Program {}\nPROGRAM again");
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "obj", "ignored.cs"), "Program");
        var database = new HostDatabase(options);
        await database.InitializeAsync();
        var project = await new ProjectService(database).AddAsync(new AddProjectRequest(projectRoot));
        var service = new WorkspaceFileSearchService(database, options);

        var listing = await service.ListAsync(new ListProjectEntriesRequest(project.ProjectId));
        var search = await service.SearchContentsAsync(new SearchProjectContentsRequest(
            project.ProjectId,
            "program",
            CaseSensitive: false,
            WholeWord: true));

        Assert.Contains(listing.Entries, entry => entry is { RelativePath: "src", IsDirectory: true });
        Assert.Contains(listing.Entries, entry => entry.RelativePath == "src/nested/Program.cs");
        Assert.DoesNotContain(listing.Entries, entry => entry.RelativePath.StartsWith("obj/", StringComparison.Ordinal));
        Assert.Equal([2, 3], search.Matches.Select(match => match.LineNumber));
        Assert.All(search.Matches, match => Assert.Equal("src/nested/Program.cs", match.RelativePath));
        Assert.All(search.Matches, match => Assert.NotEmpty(match.MatchRanges));
    }

    private static void TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }
    }
}
