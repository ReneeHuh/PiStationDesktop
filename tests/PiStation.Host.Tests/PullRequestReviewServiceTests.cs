using PiStation.Host.SourceControl;
using PiStation.Protocol.Models;
using System.Text.Json;

namespace PiStation.Host.Tests;

public sealed class PullRequestReviewServiceTests
{
    [Fact]
    public void ParsePatchTracksBothSidesAndHunkCoordinates()
    {
        var lines = SourceControlHostingService.ParsePatch("@@ -10,2 +20,3 @@ method\n context\n-old\n+new\n+added\n");

        Assert.Collection(lines,
            line => Assert.Equal(PullRequestDiffLineKind.Header, line.Kind),
            line =>
            {
                Assert.Equal(PullRequestDiffLineKind.Context, line.Kind);
                Assert.Equal(10, line.OldLine);
                Assert.Equal(20, line.NewLine);
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
                Assert.Equal(21, line.NewLine);
            },
            line =>
            {
                Assert.Equal(PullRequestDiffLineKind.Addition, line.Kind);
                Assert.Equal(22, line.NewLine);
            });
    }

    [Fact]
    public void ParsePatchHandlesZeroLengthOldHunk()
    {
        var lines = SourceControlHostingService.ParsePatch("@@ -0,0 +1,2 @@\n+first\n+second\n");

        Assert.Equal([1, 2], lines.Where(line => line.Kind == PullRequestDiffLineKind.Addition).Select(line => line.NewLine).ToArray());
        Assert.All(lines.Where(line => line.Kind == PullRequestDiffLineKind.Addition), line => Assert.Null(line.OldLine));
    }

    [Fact]
    public void ParsePatchMarksFileHeadersAsMetadata()
    {
        var lines = SourceControlHostingService.ParsePatch("diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -1 +1 @@\n-old\n+new\n");

        Assert.Contains(lines, line => line.Kind == PullRequestDiffLineKind.Metadata && line.Text.StartsWith("diff --git", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Text.StartsWith("+++", StringComparison.Ordinal) && line.Kind != PullRequestDiffLineKind.Metadata);
    }

    [Fact]
    public void SubmitCommandUsesExplicitHostAndJsonStdinPayload()
    {
        var repository = new SourceControlRepository(SourceControlProvider.GitHub, "ghe.example", "owner", "repo",
            "https://ghe.example/owner/repo", "https://ghe.example/owner/repo.git", "main", true);
        var command = SourceControlHostingService.BuildSubmitReviewCommand(repository, 42, "head-sha", PullRequestReviewEvent.Comment,
            "review body", [new PullRequestInlineComment("src/file.cs", 7, PullRequestDiffSide.Right, "line comment")]);

        Assert.Equal("repos/owner/repo/pulls/42/reviews", command.Endpoint);
        Assert.Contains("--hostname", command.Arguments);
        Assert.Equal("ghe.example", command.Arguments[Array.IndexOf(command.Arguments, "--hostname") + 1]);
        using var payload = JsonDocument.Parse(command.Input);
        Assert.Equal("head-sha", payload.RootElement.GetProperty("commit_id").GetString());
        Assert.Equal("COMMENT", payload.RootElement.GetProperty("event").GetString());
        Assert.Equal("src/file.cs", payload.RootElement.GetProperty("comments")[0].GetProperty("path").GetString());
    }

    [Fact]
    public void ParsePatchTreatsTripleMarkersInsideHunksAsContent()
    {
        var lines = SourceControlHostingService.ParsePatch("--- a/file.txt\n+++ b/file.txt\n@@ -1,2 +1,2 @@\n---not-a-header\n+++not-a-header\n");

        Assert.Equal(PullRequestDiffLineKind.Deletion, lines[^2].Kind);
        Assert.Equal(PullRequestDiffLineKind.Addition, lines[^1].Kind);
        Assert.Equal(1, lines[^2].OldLine);
        Assert.Equal(1, lines[^1].NewLine);
    }

    [Fact]
    public void ParsePatchDoesNotInventCoordinatesForMalformedOrOverflowHunks()
    {
        var lines = SourceControlHostingService.ParsePatch("@@ -999999999999999999999,1 +2,1 @@\n-old\n+new\n");

        Assert.Null(lines[1].OldLine);
        Assert.Null(lines[1].NewLine);
        Assert.Null(lines[2].OldLine);
        Assert.Null(lines[2].NewLine);
    }
}
