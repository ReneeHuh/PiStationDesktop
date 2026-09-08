using System.Diagnostics;
using PiStation.Host.Errors;
using PiStation.Host.Persistence;
using PiStation.Host.Projects;
using PiStation.Host.SourceControl;
using PiStation.Host.Workspaces;
using PiStation.Protocol.Models;

namespace PiStation.Host.Tests;

public sealed class SourceControlGenerationTests
{
    private static readonly string[] GitTestConfiguration = ["-c", "user.name=Writer tests", "-c", "user.email=writer@example.test", "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false"];
    [Fact]
    public async Task SelectedCommitUsesActualLiteralPathDiffAndPreservesStagedChanges()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "selected[1].txt"), "SELECTED_NEW_BEHAVIOR\n");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "other.txt"), "UNRELATED_STAGED_CONTENT\n");
        await GitAsync(fixture.Root, "add", "other.txt");
        var before = await File.ReadAllBytesAsync(Path.Combine(fixture.Root, ".git", "index"));
        var result = await fixture.Service.GenerateTextAsync(new(fixture.Target, false, FilePaths: ["selected[1].txt"]));
        Assert.Equal("Describe the change", result.Title);
        Assert.Contains("+SELECTED_NEW_BEHAVIOR", fixture.Writer.Prompt);
        Assert.DoesNotContain("UNRELATED_STAGED_CONTENT", fixture.Writer.Prompt);
        Assert.Equal(before, await File.ReadAllBytesAsync(Path.Combine(fixture.Root, ".git", "index")));
        Assert.Contains("other.txt", await GitAsync(fixture.Root, "diff", "--cached", "--name-only"));
    }

    [Fact]
    public async Task PullRequestUsesCommittedBranchDiffAndRepositoryTemplateOnCleanWorkingTree()
    {
        using var fixture = await Fixture.CreateAsync();
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".github"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, ".github", "pull_request_template.md"), "## Customer impact\n## Verification\n");
        await GitAsync(fixture.Root, "add", ".");
        await GitAsync(fixture.Root, "commit", "-m", "docs: add review template");
        await GitAsync(fixture.Root, "checkout", "-b", "feature/fix");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "base.txt"), "COMMITTED_BRANCH_BEHAVIOR\n");
        await GitAsync(fixture.Root, "commit", "-am", "fix: correct branch behavior");
        Assert.Equal("", await GitAsync(fixture.Root, "status", "--porcelain"));
        await fixture.Service.SaveWritingSettingsAsync(new(SourceControlWritingStyle.RepositoryConventions));
        await fixture.Service.GenerateTextAsync(new(fixture.Target, true));
        Assert.Contains("+COMMITTED_BRANCH_BEHAVIOR", fixture.Writer.Prompt);
        Assert.Contains("fix: correct branch behavior", fixture.Writer.Prompt);
        Assert.Contains("## Customer impact", fixture.Writer.Prompt);
        Assert.Contains("Base branch: main", fixture.Writer.Prompt);
        Assert.DoesNotContain("No uncommitted", fixture.Writer.Prompt);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "base.txt"), "UNCOMMITTED_WORK\n");
        await fixture.Service.GenerateTextAsync(new(fixture.Target, true));
        Assert.DoesNotContain("UNCOMMITTED_WORK", fixture.Writer.Prompt);
    }

    [Fact]
    public async Task ExplicitNonstandardBaseAndSavedModelAndInstructionsAreUsed()
    {
        using var fixture = await Fixture.CreateAsync();
        await GitAsync(fixture.Root, "branch", "-m", "trunk");
        await GitAsync(fixture.Root, "checkout", "-b", "feature");
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "base.txt"), "changed\n");
        await GitAsync(fixture.Root, "commit", "-am", "change");
        var model = new PiModelSelection("provider", "writer-model");
        await fixture.Service.SaveWritingSettingsAsync(new(SourceControlWritingStyle.Custom, "Write in Spanish", model));
        await fixture.Service.GenerateTextAsync(new(fixture.Target, true, "Explain the fix", "trunk", Model: new("other", "other")));
        Assert.Equal(model, fixture.Writer.Model);
        Assert.Contains("Write in Spanish", fixture.Writer.Prompt);
        Assert.Contains("Explain the fix", fixture.Writer.Prompt);
        Assert.Contains("Base branch: trunk", fixture.Writer.Prompt);
    }

    [Fact]
    public async Task FirstCommitSupportsUntrackedFilesWithoutCreatingRealIndex()
    {
        using var fixture = await Fixture.CreateAsync(initialCommit: false);
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "first.txt"), "INITIAL_CONTENT\n");
        await fixture.Service.GenerateTextAsync(new(fixture.Target, false));
        Assert.Contains("+INITIAL_CONTENT", fixture.Writer.Prompt);
        Assert.False(File.Exists(Path.Combine(fixture.Root, ".git", "index")));
    }

    [Fact]
    public async Task MissingBaseEmptyChangesAndEscapingPathsNeverCallPi()
    {
        using var fixture = await Fixture.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.GenerateTextAsync(new(fixture.Target, true, BaseBranch: "missing")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.GenerateTextAsync(new(fixture.Target, false)));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.GenerateTextAsync(new(fixture.Target, false, FilePaths: ["../outside"])));
        Assert.Equal(0, fixture.Writer.Calls);
    }

    [Fact]
    public async Task ChangesDuringGenerationRejectStaleText()
    {
        using var fixture = await Fixture.CreateAsync();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "base.txt"), "first change\n");
        fixture.Writer.OnGenerate = () => File.WriteAllTextAsync(Path.Combine(fixture.Root, "base.txt"), "new change\n");
        var error = await Assert.ThrowsAsync<HostOperationException>(() => fixture.Service.GenerateTextAsync(new(fixture.Target, false)));
        Assert.Contains("during generation", error.Message);
    }

    [Fact]
    public async Task WritingPreferencesPersistAndRejectStaleEdits()
    {
        using var directory = new HostTestDirectory();
        using var first = new SourceControlWritingSettingsStore(directory.Path);
        var saved = await first.SaveAsync(new(SourceControlWritingStyle.ConventionalCommits, Model: new("fake", "fake-fast")));
        using var reopened = new SourceControlWritingSettingsStore(directory.Path);
        Assert.Equal(saved, await reopened.LoadAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.SaveAsync(new()));
        await Assert.ThrowsAsync<ArgumentException>(() => reopened.SaveAsync(saved with { Model = new("", "model") }));
    }

    [Fact]
    public async Task PiWriterConsumesRpcCompletionFromSeparateProcess()
    {
        using var directory = new HostTestDirectory();
        var options = directory.CreateOptions("source-control-writer");
        var writer = new PiSourceControlTextGenerator(options);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var result = await writer.GenerateAsync(directory.CreateDirectory("project"), "Write a commit", null, timeout.Token);
        Assert.Equal("Fix the selected behavior", result.Title);
        Assert.Equal("Explain the implementation change", result.Body);
        Assert.False(Directory.Exists(options.SessionRoot));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"title\":\"\",\"body\":\"\"}")]
    [InlineData("{\"title\":\"first\\nsecond\",\"body\":\"\"}")]
    [InlineData("{\"title\":123,\"body\":\"\"}")]
    [InlineData("{\"title\":\"Fix parsing\",\"body\":null}")]
    public void InvalidModelOutputIsReportedInsteadOfInventingText(string output) =>
        Assert.Throws<InvalidOperationException>(() => PiSourceControlTextGenerator.ParseResponse(output));

    [Fact]
    public void FencedJsonSupportsEmptyCommitBody() =>
        Assert.Equal(new GeneratedSourceControlText("Fix parsing", ""), PiSourceControlTextGenerator.ParseResponse("```json\n{\"title\":\"Fix parsing\",\"body\":\"\"}\n```"));

    private sealed class RecordingWriter : ISourceControlTextGenerator
    {
        public string Prompt { get; private set; } = "";
        public PiModelSelection? Model { get; private set; }
        public int Calls { get; private set; }
        public Func<Task>? OnGenerate { get; set; }
        public async Task<GeneratedSourceControlText> GenerateAsync(string workspace, string prompt, PiModelSelection? model, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            Prompt = prompt;
            Model = model;
            if (OnGenerate is not null) await OnGenerate();
            return new("Describe the change", "Explain why");
        }
    }

    private sealed class Fixture(HostTestDirectory directory, string root, WorkspaceTarget target, SourceControlHostingService service, RecordingWriter writer) : IDisposable
    {
        public string Root { get; } = root;
        public WorkspaceTarget Target { get; } = target;
        public SourceControlHostingService Service { get; } = service;
        public RecordingWriter Writer { get; } = writer;
        public static async Task<Fixture> CreateAsync(bool initialCommit = true)
        {
            var directory = new HostTestDirectory();
            var options = directory.CreateOptions();
            var root = directory.CreateDirectory("repo");
            await GitAsync(root, "init", "-b", "main");
            if (initialCommit)
            {
                await File.WriteAllTextAsync(Path.Combine(root, "base.txt"), "baseline\n");
                await GitAsync(root, "add", ".");
                await GitAsync(root, "commit", "-m", "Initial baseline");
            }
            var database = new HostDatabase(options);
            await database.InitializeAsync();
            var projects = new ProjectService(database);
            var project = await projects.AddAsync(new(root));
            var writer = new RecordingWriter();
            var service = new SourceControlHostingService(new ThreadWorkspaceResolver(database), projects,
                textGenerator: writer, writingSettings: new(options.CanonicalDataRoot));
            return new(directory, root, new(project.ProjectId), service, writer);
        }
        public void Dispose() { Service.Dispose(); directory.Dispose(); }
    }

    private static async Task<string> GitAsync(string root, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in GitTestConfiguration.Concat(arguments)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
        return (await output).Trim();
    }
}
