using System.Text.Json.Nodes;
using PiStation.ClientRuntime;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;

namespace PiStation.ClientRuntime.Tests;

public sealed class PullRequestReviewDraftStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "PiStationDesktop.PullRequestReviewDraftStoreTests",
        Guid.NewGuid().ToString("N"));

    public PullRequestReviewDraftStoreTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task ReopenRoundTripPreservesReplyAndPendingOperation()
    {
        var projectId = ProjectId.Parse("project-roundtrip");
        var draft = CreateDraft(
            repository: "owner/repo",
            number: "42",
            pendingOperationId: CommandId.Parse("operation-roundtrip"),
            replyThreadId: "thread-7",
            replyBody: "A pending reply",
            pendingAction: "Reply");

        await new PullRequestReviewDraftStore(_root).SaveAsync(projectId, draft);
        var restored = await new PullRequestReviewDraftStore(_root)
            .LoadAsync(projectId, draft.Repository, draft.Number);

        Assert.NotNull(restored);
        Assert.Equal(draft.Repository, restored.Repository);
        Assert.Equal(draft.Number, restored.Number);
        Assert.Equal(draft.HeadCommitId, restored.HeadCommitId);
        Assert.Equal(draft.Body, restored.Body);
        Assert.Equal(draft.Event, restored.Event);
        Assert.Equal(draft.Comments, restored.Comments);
        Assert.Equal(draft.PendingOperationId, restored.PendingOperationId);
        Assert.Equal(draft.ReplyThreadId, restored.ReplyThreadId);
        Assert.Equal(draft.ReplyBody, restored.ReplyBody);
        Assert.Equal(draft.PendingAction, restored.PendingAction);
    }

    [Theory]
    [InlineData("SubmitReview")]
    [InlineData("Reply")]
    [InlineData("Resolve")]
    [InlineData("Unresolve")]
    public async Task AllRecoveryPendingActionsSurvivePersistence(string action)
    {
        var projectId = ProjectId.Parse("project-pending-actions");
        var store = new PullRequestReviewDraftStore(_root);
        var draft = CreateDraft(
            "owner/repo",
            "42",
            pendingOperationId: CommandId.Parse("operation-action"),
            pendingAction: action);

        await store.SaveAsync(projectId, draft);

        var restored = await store.LoadAsync(projectId, draft.Repository, draft.Number);
        Assert.Equal(action, restored?.PendingAction);
        Assert.Equal(draft.PendingOperationId, restored?.PendingOperationId);
    }

    [Fact]
    public async Task DraftsAreIsolatedByProjectRepositoryAndPullRequest()
    {
        var projectOne = ProjectId.Parse("project-one");
        var projectTwo = ProjectId.Parse("project-two");
        var store = new PullRequestReviewDraftStore(_root);
        var first = CreateDraft("owner/repo", "42") with { Body = "project one / PR 42" };
        var samePrOtherProject = CreateDraft("owner/repo", "42") with { Body = "project two / PR 42" };
        var otherRepository = CreateDraft("other/repo", "42") with { Body = "other repository" };
        var otherPullRequest = CreateDraft("owner/repo", "43") with { Body = "other pull request" };

        await store.SaveAsync(projectOne, first);
        await store.SaveAsync(projectTwo, samePrOtherProject);
        await store.SaveAsync(projectOne, otherRepository);
        await store.SaveAsync(projectOne, otherPullRequest);

        Assert.Equal(first.Body, (await store.LoadAsync(projectOne, "owner/repo", "42"))?.Body);
        Assert.Equal(samePrOtherProject.Body, (await store.LoadAsync(projectTwo, "owner/repo", "42"))?.Body);
        Assert.Equal(otherRepository.Body, (await store.LoadAsync(projectOne, "other/repo", "42"))?.Body);
        Assert.Equal(otherPullRequest.Body, (await store.LoadAsync(projectOne, "owner/repo", "43"))?.Body);
        Assert.Null(await store.LoadAsync(ProjectId.Parse("missing-project"), "owner/repo", "42"));
    }

    [Fact]
    public async Task DeleteRemovesOnlyTheSelectedTarget()
    {
        var projectId = ProjectId.Parse("project-delete");
        var store = new PullRequestReviewDraftStore(_root);
        await store.SaveAsync(projectId, CreateDraft("owner/repo", "42"));
        await store.SaveAsync(projectId, CreateDraft("owner/repo", "43"));
        await store.SaveAsync(projectId, CreateDraft("other/repo", "42"));

        await store.DeleteAsync(projectId, "owner/repo", "42");

        Assert.Null(await store.LoadAsync(projectId, "owner/repo", "42"));
        Assert.NotNull(await store.LoadAsync(projectId, "owner/repo", "43"));
        Assert.NotNull(await store.LoadAsync(projectId, "other/repo", "42"));
    }

    [Fact]
    public async Task ConcurrentWritesAlwaysLeaveAValidDraft()
    {
        var projectId = ProjectId.Parse("project-concurrent");
        var store = new PullRequestReviewDraftStore(_root);
        var drafts = Enumerable.Range(0, 24)
            .Select(index => CreateDraft("owner/repo", "42") with
            {
                Body = $"concurrent-body-{index}",
                PendingOperationId = CommandId.Parse($"operation-{index}")
            })
            .ToArray();

        await Task.WhenAll(drafts.Select(draft => store.SaveAsync(projectId, draft)));

        var restored = await store.LoadAsync(projectId, "owner/repo", "42");
        Assert.NotNull(restored);
        Assert.Contains(restored.Body, drafts.Select(draft => draft.Body));
        Assert.NotNull(restored.PendingOperationId);
    }

    [Fact]
    public async Task HashedPathsCannotEscapeRootEvenForTraversalLikeKeys()
    {
        var projectId = ProjectId.Parse("project-paths");
        var repository = "..\\..\\outside\\repo";
        var number = "42";
        var store = new PullRequestReviewDraftStore(_root);
        await store.SaveAsync(projectId, CreateDraft(repository, number));

        var root = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).ToArray();
        Assert.NotEmpty(files);
        Assert.All(files, path => Assert.StartsWith(root, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        Assert.Equal("unsafe path body", (await store.LoadAsync(projectId, repository, number))?.Body);
    }

    [Fact]
    public async Task OversizeAndCorruptFilesThrowWithoutBeingSilentlyForgotten()
    {
        var projectId = ProjectId.Parse("project-corrupt");
        var repository = "owner/repo";
        var number = "42";
        var store = new PullRequestReviewDraftStore(_root);
        await store.SaveAsync(projectId, CreateDraft(repository, number));
        var path = FindDraftFile();

        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        json["body"] = new string('x', PullRequestReviewDefaults.MaximumBodyCharacters + 1);
        await File.WriteAllTextAsync(path, json.ToJsonString());
        var oversize = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.LoadAsync(projectId, repository, number));
        Assert.Contains("draft", oversize.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(path));

        await File.WriteAllTextAsync(path, "{ definitely not valid json");
        var corrupt = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.LoadAsync(projectId, repository, number));
        Assert.Contains("draft", corrupt.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task MoreThanFiftyCommentsAreRejectedWithoutDeletingExistingFile()
    {
        var projectId = ProjectId.Parse("project-comment-limit");
        var repository = "owner/repo";
        var number = "42";
        var store = new PullRequestReviewDraftStore(_root);
        await store.SaveAsync(projectId, CreateDraft(repository, number));
        var path = FindDraftFile();
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        var comments = json["comments"]!.AsArray();
        var original = comments[0]!.DeepClone();
        for (var index = comments.Count; index < PullRequestReviewDefaults.MaximumInlineComments + 1; index++)
        {
            comments.Add(original!.DeepClone());
        }

        await File.WriteAllTextAsync(path, json.ToJsonString());
        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => store.LoadAsync(projectId, repository, number));
        Assert.Contains("draft", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task SaveRejectsOversizeAndUnboundedOperationFieldsWithoutReplacingDraft()
    {
        var projectId = ProjectId.Parse("project-save-bounds");
        var store = new PullRequestReviewDraftStore(_root);
        var valid = CreateDraft("owner/repo", "42");
        await store.SaveAsync(projectId, valid);
        var invalidDrafts = new[]
        {
            valid with { Body = new string('x', PullRequestReviewDefaults.MaximumBodyCharacters + 1) },
            valid with { ReplyBody = new string('x', PullRequestReviewDefaults.MaximumBodyCharacters + 1) },
            valid with { ReplyThreadId = new string('x', 257) },
            valid with { HeadCommitId = new string('x', 129) },
            valid with { Repository = new string('x', 1025) },
            valid with { Number = new string('1', 21) },
            valid with { PendingOperationId = CommandId.Parse(new string('x', 129)) },
            valid with { Comments = Enumerable.Repeat(valid.Comments[0], PullRequestReviewDefaults.MaximumInlineComments + 1).ToArray() },
        };

        foreach (var invalid in invalidDrafts)
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => store.SaveAsync(projectId, invalid));
            Assert.Equal(valid.Body, (await store.LoadAsync(projectId, valid.Repository, valid.Number))?.Body);
        }
    }

    private string FindDraftFile()
    {
        var files = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return Assert.Single(files);
    }

    private static PullRequestReviewDraft CreateDraft(
        string repository,
        string number,
        CommandId? pendingOperationId = null,
        string? replyThreadId = null,
        string? replyBody = null,
        string? pendingAction = null) => new(
        repository,
        number,
        "head-sha",
        repository.Contains("outside", StringComparison.Ordinal) ? "unsafe path body" : "review body",
        PullRequestReviewEvent.Comment,
        [new PullRequestInlineComment("src/App.cs", 18, PullRequestDiffSide.Right, "Please guard this.")],
        PendingOperationId: pendingOperationId,
        ReplyThreadId: replyThreadId,
        ReplyBody: replyBody ?? string.Empty,
        PendingAction: pendingAction);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
