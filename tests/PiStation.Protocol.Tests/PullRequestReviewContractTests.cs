using System.Globalization;
using System.Text.Json;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Serialization;

namespace PiStation.Protocol.Tests;

public sealed class PullRequestReviewContractTests
{
    [Fact]
    public void ReviewContinuationsRoundTripEveryCollectionAndPatchOffsets()
    {
        var snapshot = CreateSnapshot();
        var pages = Enum.GetValues<PullRequestReviewPageKind>().Select(kind => new PullRequestReviewContinuation(kind,
            "cursor", PullRequestReviewDefaults.RepositoryKey(snapshot.Repository), snapshot.PullRequest.Number,
            snapshot.HeadCommitId, kind == PullRequestReviewPageKind.ThreadComments ? "thread" : null, snapshot.BaseCommitId)).ToArray();
        snapshot = snapshot with { NextPages = pages, Files = [snapshot.Files[0] with { PatchLineOffset = 20_000 }] };
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(snapshot, ProtocolJsonContext.Default.PullRequestReviewSnapshot),
            ProtocolJsonContext.Default.PullRequestReviewSnapshot)!;
        Assert.Equal(pages, restored.NextPages);
        Assert.Equal(20_000, Assert.Single(restored.Files).PatchLineOffset);
        foreach (var page in pages)
        {
            var request = new GetPullRequestReviewRequest(new(ProjectId.New()), snapshot.PullRequest.Number, page);
            var roundTrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(request, ProtocolJsonContext.Default.GetPullRequestReviewRequest),
                ProtocolJsonContext.Default.GetPullRequestReviewRequest);
            Assert.Equal(request, roundTrip);
        }
    }

    [Fact]
    public void GlobalSearchContinuationsAndIncompleteScanNoticesRoundTrip()
    {
        var request = new GlobalSearchRequest("needle", Continuation: "resume");
        var result = new GlobalSearchResult([], true, "next", "Skipped oversized entry");
        Assert.Equal(request, JsonSerializer.Deserialize(JsonSerializer.Serialize(request, ProtocolJsonContext.Default.GlobalSearchRequest), ProtocolJsonContext.Default.GlobalSearchRequest));
        var restored = JsonSerializer.Deserialize(JsonSerializer.Serialize(result, ProtocolJsonContext.Default.GlobalSearchResult), ProtocolJsonContext.Default.GlobalSearchResult)!;
        Assert.Equal(result.NextContinuation, restored.NextContinuation);
        Assert.Equal(result.Notice, restored.Notice);
        Assert.True(restored.IsTruncated);
    }

    [Fact]
    public void ReviewSnapshotRoundTripsNumberLinesSidesMetadataAndMissingPatches()
    {
        var snapshot = CreateSnapshot();

        var json = JsonSerializer.Serialize(
            snapshot,
            ProtocolJsonContext.Default.PullRequestReviewSnapshot);
        var restored = JsonSerializer.Deserialize(
            json,
            ProtocolJsonContext.Default.PullRequestReviewSnapshot);

        Assert.NotNull(restored);
        Assert.Equal(snapshot.Repository, restored.Repository);
        Assert.Equal(snapshot.PullRequest.Provider, restored.PullRequest.Provider);
        Assert.Equal(snapshot.PullRequest.Repository, restored.PullRequest.Repository);
        Assert.Equal(snapshot.PullRequest.Number, restored.PullRequest.Number);
        Assert.Equal(snapshot.PullRequest.Title, restored.PullRequest.Title);
        Assert.Equal(snapshot.PullRequest.Url, restored.PullRequest.Url);
        Assert.Equal(snapshot.PullRequest.State, restored.PullRequest.State);
        Assert.Equal(snapshot.PullRequest.Author, restored.PullRequest.Author);
        Assert.Equal(snapshot.PullRequest.SourceBranch, restored.PullRequest.SourceBranch);
        Assert.Equal(snapshot.PullRequest.TargetBranch, restored.PullRequest.TargetBranch);
        Assert.Equal(snapshot.PullRequest.IsDraft, restored.PullRequest.IsDraft);
        Assert.Equal(snapshot.PullRequest.Labels, restored.PullRequest.Labels);
        Assert.Equal(snapshot.PullRequest.Reviewers, restored.PullRequest.Reviewers);
        Assert.Equal(snapshot.PullRequest.Checks, restored.PullRequest.Checks);
        Assert.Equal(snapshot.PullRequest.UpdatedUtc, restored.PullRequest.UpdatedUtc);
        Assert.Equal(snapshot.Body, restored.Body);
        Assert.Equal(snapshot.HeadCommitId, restored.HeadCommitId);
        Assert.Equal(snapshot.BaseCommitId, restored.BaseCommitId);
        Assert.Equal(snapshot.ViewerLogin, restored.ViewerLogin);
        Assert.Equal(snapshot.IsTruncated, restored.IsTruncated);
        Assert.Equal(snapshot.Notice, restored.Notice);

        var file = Assert.Single(restored.Files, item => item.Path == "src/New.cs");
        Assert.Equal("src/Old.cs", file.PreviousPath);
        Assert.Equal("renamed", file.Status);
        Assert.False(file.PatchUnavailable);
        Assert.Collection(
            file.Lines,
            line =>
            {
                Assert.Equal(PullRequestDiffLineKind.Header, line.Kind);
                Assert.Null(line.OldLine);
                Assert.Null(line.NewLine);
            },
            line =>
            {
                Assert.Equal(PullRequestDiffLineKind.Metadata, line.Kind);
                Assert.Null(line.OldLine);
                Assert.Null(line.NewLine);
            },
            line =>
            {
                Assert.Equal(PullRequestDiffLineKind.Context, line.Kind);
                Assert.Equal(10, line.OldLine);
                Assert.Equal(10, line.NewLine);
            },
            line =>
            {
                Assert.Equal(PullRequestDiffLineKind.Deletion, line.Kind);
                Assert.Equal(11, line.OldLine);
                Assert.Null(line.NewLine);
            },
            line =>
            {
                Assert.Equal(PullRequestDiffLineKind.Addition, line.Kind);
                Assert.Null(line.OldLine);
                Assert.Equal(11, line.NewLine);
            });

        var unavailable = Assert.Single(restored.Files, item => item.Path == "assets/image.bin");
        Assert.True(unavailable.PatchUnavailable);
        Assert.Empty(unavailable.Lines);

        var discussion = Assert.Single(restored.Discussions);
        Assert.Equal(42, discussion.Line);
        Assert.Equal(PullRequestDiffSide.Right, discussion.Side);
        Assert.True(discussion.IsResolved);
        Assert.Equal("Looks good", Assert.Single(discussion.Comments).Body);
        Assert.DoesNotContain("rawPatch", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DraftRoundTripsReplyAndPendingOperationIdentity()
    {
        var draft = new PullRequestReviewDraft(
            "owner/repo",
            "42",
            "head-sha",
            "Please fix the edge case.",
            PullRequestReviewEvent.Comment,
            [new PullRequestInlineComment("src/App.cs", 18, PullRequestDiffSide.Left, "This needs a guard.")],
            PendingOperationId: CommandId.Parse("operation-42"),
            ReplyThreadId: "thread-7",
            ReplyBody: "Following up while the operation is recoverable.",
            PendingAction: "Reply");

        var json = JsonSerializer.Serialize(draft, ProtocolJsonContext.Default.PullRequestReviewDraft);
        var restored = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.PullRequestReviewDraft);

        Assert.NotNull(restored);
        Assert.Equal(draft.Repository, restored.Repository);
        Assert.Equal(draft.Number, restored.Number);
        Assert.Equal(draft.HeadCommitId, restored.HeadCommitId);
        Assert.Equal(draft.Body, restored.Body);
        Assert.Equal(draft.Event, restored.Event);
        Assert.Equal(draft.PendingOperationId, restored.PendingOperationId);
        Assert.Equal("thread-7", restored.ReplyThreadId);
        Assert.Equal(draft.ReplyBody, restored.ReplyBody);
        Assert.Equal("Reply", restored.PendingAction);
        var comment = Assert.Single(restored.Comments);
        Assert.Equal("src/App.cs", comment.Path);
        Assert.Equal(18, comment.Line);
        Assert.Equal(PullRequestDiffSide.Left, comment.Side);
        Assert.Equal("This needs a guard.", comment.Body);
    }

    [Fact]
    public void ReviewWriteRequestsPreserveRepositoryHeadAndOperationIdentity()
    {
        var target = new PullRequestReviewTarget(
            new WorkspaceTarget(ProjectId.Parse("project-review"), ThreadId.Parse("thread-review")),
            "owner/repo",
            "42",
            "head-sha");
        var request = new SubmitPullRequestReviewRequest(
            target,
            PullRequestReviewEvent.RequestChanges,
            "Please address this.",
            [new PullRequestInlineComment("src/App.cs", 18, PullRequestDiffSide.Right, "Guard this path.")],
            CommandId.Parse("operation-submit"));

        var json = JsonSerializer.Serialize(
            request,
            ProtocolJsonContext.Default.SubmitPullRequestReviewRequest);
        var restored = JsonSerializer.Deserialize(
            json,
            ProtocolJsonContext.Default.SubmitPullRequestReviewRequest);

        Assert.NotNull(restored);
        Assert.Equal(target, restored.Target);
        Assert.Equal(request.Event, restored.Event);
        Assert.Equal(request.Body, restored.Body);
        Assert.Equal(request.OperationId, restored.OperationId);
        Assert.Equal(request.Comments, restored.Comments);
    }

    [Fact]
    public void ReviewCapabilitiesFollowImplementedProviders()
    {
        Assert.True(HostingCapabilities.CanReadReview(SourceControlProvider.GitHub));
        Assert.True(HostingCapabilities.CanWriteReview(SourceControlProvider.GitHub));

        foreach (var provider in new[]
        {
            SourceControlProvider.Unknown,
            SourceControlProvider.Bitbucket,
        })
        {
            Assert.False(HostingCapabilities.CanReadReview(provider));
            Assert.False(HostingCapabilities.CanWriteReview(provider));
        }
        Assert.True(HostingCapabilities.CanReadReview(SourceControlProvider.GitLab));
        Assert.True(HostingCapabilities.CanWriteReview(SourceControlProvider.GitLab));
        Assert.True(HostingCapabilities.CanReadReview(SourceControlProvider.AzureDevOps));
        Assert.False(HostingCapabilities.CanWriteReview(SourceControlProvider.AzureDevOps));
    }

    private static PullRequestReviewSnapshot CreateSnapshot()
    {
        var repository = new SourceControlRepository(
            SourceControlProvider.GitHub,
            "github.com",
            "owner",
            "repo",
            "https://github.com/owner/repo",
            "git@github.com:owner/repo.git",
            "main",
            true);
        var descriptor = new PullRequestDescriptor(
            SourceControlProvider.GitHub,
            "owner/repo",
            "42",
            "Review me",
            "https://github.com/owner/repo/pull/42",
            PullRequestState.Open,
            "reviewer",
            "feature",
            "main",
            false,
            ["review"],
            ["maintainer"],
            PullRequestCheckState.Passed,
            DateTimeOffset.Parse("2026-09-06T12:00:00Z", CultureInfo.InvariantCulture));
        var lines = new[]
        {
            new PullRequestDiffLine(null, null, "@@ -10,2 +10,2 @@", PullRequestDiffLineKind.Header),
            new PullRequestDiffLine(null, null, "rename from src/Old.cs", PullRequestDiffLineKind.Metadata),
            new PullRequestDiffLine(10, 10, " unchanged", PullRequestDiffLineKind.Context),
            new PullRequestDiffLine(11, null, "-old", PullRequestDiffLineKind.Deletion),
            new PullRequestDiffLine(null, 11, "+new", PullRequestDiffLineKind.Addition),
        };
        var files = new[]
        {
            new PullRequestChangedFile("src/New.cs", "src/Old.cs", "renamed", 1, 1, lines),
            new PullRequestChangedFile("assets/image.bin", null, "modified", 0, 0, [], PatchUnavailable: true),
        };
        var discussion = new PullRequestDiscussion(
            "discussion-1",
            "src/New.cs",
            42,
            PullRequestDiffSide.Right,
            true,
            false,
            true,
            true,
            [new PullRequestReviewComment(
                "comment-1",
                "reviewer",
                "Looks good",
                DateTimeOffset.Parse("2026-09-06T12:01:00Z", CultureInfo.InvariantCulture),
                "https://github.com/owner/repo/pull/42#discussion_r1")]);

        return new PullRequestReviewSnapshot(
            repository,
            descriptor,
            "A body with the complete review context.",
            "head-sha",
            "base-sha",
            "reviewer",
            [new PullRequestCommit(
                "commit-sha",
                "Refine review",
                "reviewer",
                DateTimeOffset.Parse("2026-09-06T11:00:00Z", CultureInfo.InvariantCulture))],
            [new PullRequestCheck("build", "completed", "success")],
            files,
            [discussion],
            IsTruncated: true,
            Notice: "Some provider patches were omitted.");
    }
}
