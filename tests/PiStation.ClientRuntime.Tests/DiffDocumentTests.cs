namespace PiStation.ClientRuntime.Tests;

public sealed class DiffDocumentTests
{
    [Fact]
    public void DecodesGitQuotedPathsAndRejectsOverflowingHunkNumbers()
    {
        const string patch = "diff --git \"a/caf\\303\\251.cs\" \"b/caf\\303\\251.cs\"\n+++ \"b/caf\\303\\251.cs\"\n@@ -1 +1 @@\n+new";
        Assert.Equal("café.cs", DiffDocument.Parse(patch)[^1].Path);
        var malformed = DiffDocument.Parse("@@ -999999999999 +1 @@\n+new");
        Assert.Null(malformed[^1].NewLine);
    }

    [Fact]
    public void TracksOldAndNewSourceLinesAcrossHunksAndPreservesSelectionOffsets()
    {
        const string patch = "diff --git a/src/app.cs b/src/app.cs\n--- a/src/app.cs\n+++ b/src/app.cs\n@@ -10,2 +10,3 @@\n keep\n-old\n+new\n+extra\n@@ -40 +41 @@\n-again\n+fixed\n";
        var lines = DiffDocument.Parse(patch);
        var old = Assert.Single(lines, static line => line.Text == "-old");
        var extra = Assert.Single(lines, static line => line.Text == "+extra");
        Assert.Equal(11, old.OldLine);
        Assert.Null(old.NewLine);
        Assert.Equal(12, extra.NewLine);
        Assert.Equal("src/app.cs", extra.Path);
        Assert.Equal("+extra", patch.Substring(extra.Offset, extra.Length));
        Assert.Equal(41, lines.Single(static line => line.Text == "+fixed").NewLine);
        Assert.Equal(2, lines.Count(static line => line.Kind == DiffLineKind.Hunk));
    }
}
