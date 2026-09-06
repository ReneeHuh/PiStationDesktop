using System.Diagnostics;
using PiStation.Host.Errors;
using PiStation.Host.Git;
using PiStation.Host.Persistence;
using PiStation.Host.Projects;
using PiStation.Host.Workspaces;
using PiStation.Protocol;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;

namespace PiStation.Host.Tests;

public sealed class WorkspaceGitCommandServiceTests
{
    [Fact]
    public async Task InitBranchesSelectedCommitPushAndPullCompleteThroughReceipts()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var projectRoot = temporaryDirectory.CreateDirectory("project");
        var database = new HostDatabase(options);
        await database.InitializeAsync();
        var project = await new ProjectService(database).AddAsync(new AddProjectRequest(projectRoot));
        var service = CreateService(database, options);
        var clientId = ClientId.New();

        var initId = CommandId.New();
        var initialized = await ExecuteAsync(
            service,
            database,
            clientId,
            initId,
            project.ProjectId,
            new GitInitCommand());
        var replay = await ExecuteAsync(
            service,
            database,
            clientId,
            initId,
            project.ProjectId,
            new GitInitCommand());

        Assert.Equal(CommandReceiptState.Completed, initialized.Receipt.State);
        Assert.Equal("initialized", initialized.Result?.Status);
        Assert.Equal(initialized, replay);
        ConfigureIdentity(projectRoot);
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "README.md"), "baseline\n");
        RunGit(projectRoot, "add", "README.md");
        RunGit(projectRoot, "commit", "--quiet", "-m", "baseline");

        var branch = await ExecuteAsync(
            service,
            database,
            clientId,
            CommandId.New(),
            project.ProjectId,
            new GitCreateBranchCommand("feature/test"));
        var refs = await service.ListRefsAsync(new ListGitRefsRequest(new WorkspaceTarget(project.ProjectId)));
        Assert.Equal(CommandReceiptState.Completed, branch.Receipt.State);
        Assert.Contains(refs.Refs, static item => item.Name == "feature/test" && !item.IsRemote);

        await File.WriteAllTextAsync(Path.Combine(projectRoot, "a.txt"), "selected\n");
        await File.WriteAllTextAsync(Path.Combine(projectRoot, "b.txt"), "already staged\n");
        RunGit(projectRoot, "add", "b.txt");
        var commit = await ExecuteAsync(
            service,
            database,
            clientId,
            CommandId.New(),
            project.ProjectId,
            new GitRunActionCommand(GitActionKind.Commit, FilePaths: ["a.txt"]));
        Assert.Equal(CommandReceiptState.Completed, commit.Receipt.State);
        Assert.Equal("a.txt", RunGit(projectRoot, "show", "--pretty=format:", "--name-only", "HEAD").Trim());
        Assert.Equal("b.txt", RunGit(projectRoot, "diff", "--cached", "--name-only").Trim());

        RunGit(projectRoot, "commit", "--quiet", "-m", "commit staged b");
        var remoteRoot = temporaryDirectory.CreateDirectory("remote.git");
        RunGit(remoteRoot, "init", "--bare", "--quiet");
        RunGit(projectRoot, "remote", "add", "origin", remoteRoot);
        var pushed = await ExecuteAsync(
            service,
            database,
            clientId,
            CommandId.New(),
            project.ProjectId,
            new GitRunActionCommand(GitActionKind.Push));
        Assert.Equal(CommandReceiptState.Completed, pushed.Receipt.State);

        var otherRoot = temporaryDirectory.CreateDirectory("other");
        RunGit(temporaryDirectory.Path, "clone", "--quiet", "--branch", "main", remoteRoot, otherRoot);
        ConfigureIdentity(otherRoot);
        await File.WriteAllTextAsync(Path.Combine(otherRoot, "remote.txt"), "from remote\n");
        RunGit(otherRoot, "add", "remote.txt");
        RunGit(otherRoot, "commit", "--quiet", "-m", "remote change");
        RunGit(otherRoot, "push", "--quiet");

        var pulled = await ExecuteAsync(
            service,
            database,
            clientId,
            CommandId.New(),
            project.ProjectId,
            new GitPullCommand());
        Assert.Equal(CommandReceiptState.Completed, pulled.Receipt.State);
        Assert.Equal("pulled", pulled.Result?.Status);
        Assert.True(File.Exists(Path.Combine(projectRoot, "remote.txt")));
    }

    [Fact]
    public async Task WorktreeLifecycleRoutesThreadAndRequiresExactConfirmedOwnership()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var projectRoot = temporaryDirectory.CreateDirectory("project");
        InitializeRepository(projectRoot);
        var database = new HostDatabase(options);
        await database.InitializeAsync();
        var projects = new ProjectService(database);
        var project = await projects.AddAsync(new AddProjectRequest(projectRoot));
        var thread = await projects.CreateThreadAsync(new CreateThreadRequest(project.ProjectId));
        var service = CreateService(database, options);
        var clientId = ClientId.New();

        var created = await ExecuteAsync(
            service,
            database,
            clientId,
            CommandId.New(),
            project.ProjectId,
            new GitCreateWorktreeCommand("main", AssignToThreadId: thread.ThreadId));
        var updated = await database.GetThreadAsync(thread.ThreadId);
        Assert.Equal(CommandReceiptState.Completed, created.Receipt.State);
        Assert.Equal(ThreadWorkspaceMode.Worktree, updated?.WorkspaceMode);
        Assert.True(Directory.Exists(updated?.WorktreePath));

        await File.WriteAllTextAsync(Path.Combine(updated!.WorktreePath!, "dirty.txt"), "dirty\n");
        var unconfirmed = await ExecuteAsync(
            service,
            database,
            clientId,
            CommandId.New(),
            project.ProjectId,
            new GitRemoveWorktreeCommand(updated.WorktreePath!),
            thread.ThreadId);
        Assert.Equal(CommandReceiptState.Rejected, unconfirmed.Receipt.State);
        Assert.Equal(ProtocolErrorCodes.GitDirtyWorktree, unconfirmed.Receipt.ErrorCode);

        var wrongPath = Path.Combine(options.WorktreeRoot, project.ProjectId.Value, "not-owned");
        var wrong = await ExecuteAsync(
            service,
            database,
            clientId,
            CommandId.New(),
            project.ProjectId,
            new GitRemoveWorktreeCommand(wrongPath, true, wrongPath),
            thread.ThreadId);
        Assert.Equal(ProtocolErrorCodes.WorktreeOwnershipMismatch, wrong.Receipt.ErrorCode);

        var removed = await ExecuteAsync(
            service,
            database,
            clientId,
            CommandId.New(),
            project.ProjectId,
            new GitRemoveWorktreeCommand(updated.WorktreePath!, true, updated.WorktreePath),
            thread.ThreadId);
        var restored = await database.GetThreadAsync(thread.ThreadId);
        Assert.Equal(CommandReceiptState.Completed, removed.Receipt.State);
        Assert.Equal(ThreadWorkspaceMode.Local, restored?.WorkspaceMode);
        Assert.False(Directory.Exists(updated.WorktreePath));
    }

    [Fact]
    public async Task ProjectConfigurationLoadsDefaultModeScriptsAndDurableTrust()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var projectRoot = temporaryDirectory.CreateDirectory("project");
        await File.WriteAllTextAsync(
            Path.Combine(projectRoot, "t3.json"),
            """
            {
              "defaultThreadEnvMode": "worktree",
              "iconPath": "brand.png",
              "defaultModel": { "provider": "fake", "model": "fake-standard" },
              "defaultThinkingLevel": "high",
              "defaultRuntimeMode": "plan",
              "autoPullDefaultBranch": true,
              "scripts": [
                { "name": "Setup", "command": "dotnet restore", "icon": "configure", "runOnWorktreeCreate": true },
                { "name": "Tests", "command": "dotnet test", "icon": "test" }
              ]
            }
            """);
        await File.WriteAllBytesAsync(Path.Combine(projectRoot, "brand.png"), [137, 80, 78, 71]);
        var database = new HostDatabase(temporaryDirectory.CreateOptions());
        await database.InitializeAsync();
        var projects = new ProjectService(database);

        var project = await projects.AddAsync(new AddProjectRequest(projectRoot));
        var trusted = await projects.SetScriptsTrustAsync(new SetProjectScriptsTrustRequest(project.ProjectId, true));
        var reloaded = Assert.Single(await projects.ListAsync());

        Assert.Equal(ThreadWorkspaceMode.Worktree, project.DefaultWorkspaceMode);
        Assert.Equal("dotnet restore", project.Scripts![0].Command);
        Assert.Equal("dotnet test", project.Scripts[1].Command);
        Assert.Equal(Path.Combine(projectRoot, "brand.png"), project.Icon);
        Assert.Equal(new PiModelSelection("fake", "fake-standard"), project.DefaultModel);
        Assert.Equal(PiThinkingLevel.High, project.DefaultThinkingLevel);
        Assert.Equal("plan", project.DefaultRuntimeModeId);
        Assert.True(project.AutoPullDefaultBranch);
        Assert.True(trusted.AreRepositoryScriptsTrusted);
        Assert.True(reloaded.AreRepositoryScriptsTrusted);
    }

    [Fact]
    public async Task TrustedWorktreeSetupScriptRunsInTheThreadWorkspaceAndPersistsCompletion()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var projectRoot = temporaryDirectory.CreateDirectory("setup-project");
        InitializeRepository(projectRoot);
        await File.WriteAllTextAsync(
            Path.Combine(projectRoot, "t3.json"),
            """
            {
              "scripts": [
                {
                  "name": "Setup worktree",
                  "command": "Set-Content -Path setup-result.txt -Value ready",
                  "runOnWorktreeCreate": true
                }
              ]
            }
            """);
        await using var environment = await EnvironmentService.CreateAsync(temporaryDirectory.CreateOptions());
        var project = await environment.AddProjectAsync(new AddProjectRequest(projectRoot));
        _ = await environment.SetProjectScriptsTrustAsync(new SetProjectScriptsTrustRequest(project.ProjectId, true));

        var thread = await environment.CreateThreadAsync(new CreateThreadRequest(
            project.ProjectId,
            "Setup thread",
            ThreadWorkspaceMode.Worktree,
            BaseBranch: "main"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (thread.SetupScriptState == SetupScriptState.Running)
        {
            await Task.Delay(50, timeout.Token);
            thread = await environment.GetThreadAsync(thread.ThreadId, timeout.Token);
        }

        Assert.Equal(SetupScriptState.Succeeded, thread.SetupScriptState);
        Assert.Equal("ready", (await File.ReadAllTextAsync(
            Path.Combine(thread.WorktreePath!, "setup-result.txt"),
            timeout.Token)).Trim());
    }

    [Fact]
    public async Task ReusingAWorkspaceCommandIdWithDifferentBodyIsRejected()
    {
        using var temporaryDirectory = new HostTestDirectory();
        var options = temporaryDirectory.CreateOptions();
        var projectRoot = temporaryDirectory.CreateDirectory("project");
        var database = new HostDatabase(options);
        await database.InitializeAsync();
        var project = await new ProjectService(database).AddAsync(new AddProjectRequest(projectRoot));
        var service = CreateService(database, options);
        var clientId = ClientId.New();
        var commandId = CommandId.New();
        _ = await ExecuteAsync(service, database, clientId, commandId, project.ProjectId, new GitInitCommand());

        var exception = await Assert.ThrowsAsync<HostOperationException>(() => ExecuteAsync(
            service,
            database,
            clientId,
            commandId,
            project.ProjectId,
            new GitInitCommand("develop")));
        Assert.Equal(ProtocolErrorCodes.CommandConflict, exception.Code);
    }

    private static WorkspaceGitCommandService CreateService(HostDatabase database, HostOptions options)
    {
        var resolver = new ThreadWorkspaceResolver(database);
        return new WorkspaceGitCommandService(
            database,
            new WorkspaceGitService(database, resolver),
            resolver,
            options);
    }

    private static Task<ExecuteWorkspaceGitCommandResult> ExecuteAsync(
        WorkspaceGitCommandService service,
        HostDatabase database,
        ClientId clientId,
        CommandId commandId,
        ProjectId projectId,
        WorkspaceGitCommand command,
        ThreadId? threadId = null) =>
        service.ExecuteAsync(new ExecuteWorkspaceGitCommandRequest(
            ProtocolVersion.Current,
            database.EnvironmentId,
            clientId,
            commandId,
            new WorkspaceTarget(projectId, threadId),
            command));

    private static void InitializeRepository(string projectRoot)
    {
        RunGit(projectRoot, "init", "--quiet", "--initial-branch=main");
        ConfigureIdentity(projectRoot);
        File.WriteAllText(Path.Combine(projectRoot, "README.md"), "baseline\n");
        RunGit(projectRoot, "add", "README.md");
        RunGit(projectRoot, "commit", "--quiet", "-m", "baseline");
    }

    private static void ConfigureIdentity(string projectRoot)
    {
        RunGit(projectRoot, "config", "user.email", "pistation@example.invalid");
        RunGit(projectRoot, "config", "user.name", "Pi Station Tests");
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Git did not start.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }
}
