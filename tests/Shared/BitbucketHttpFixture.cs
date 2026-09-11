using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PiStation.Host.SourceControl;

namespace PiStation.TestFixtures;

internal sealed class BitbucketHttpFixture : HttpMessageHandler
{
    public const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string Base = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    public const string Viewer = "{11111111-1111-1111-1111-111111111111}";
    public const string Reviewer = "{22222222-2222-2222-2222-222222222222}";
    public string CurrentHead { get; set; } = Head;
    public string CurrentBase { get; set; } = Base;
    public string CurrentViewer { get; set; } = Viewer;
    public string Title { get; set; } = "Bitbucket fixture";
    public string Description { get; set; } = "Description";
    public string CommentBody { get; set; } = "Please check";
    public string State { get; set; } = "OPEN";
    public bool ForeignRepository { get; set; }
    public bool ForeignComment { get; set; }
    public bool Renamed { get; set; }
    public bool MissingDiff { get; set; }
    public bool MoreComments { get; set; }
    public bool InvalidAcknowledgement { get; set; }
    public int FailWrite { get; set; }
    public int DetailReads { get; private set; }
    public Action<int>? AfterWrite { get; set; }
    public Action<int>? OnDetailRead { get; set; }
    public Func<HttpRequestMessage, HttpResponseMessage?>? Override { get; set; }
    public ConcurrentQueue<(string Method, string Path, string? Body)> Writes { get; } = new();
    public ConcurrentQueue<string> Reads { get; } = new();
    public BitbucketCloudClient Client(bool authenticated = true) => new(this, () => authenticated ? new AuthenticationHeaderValue("Bearer", "isolated-test-credential") : null);
    private static object Actor(string uuid) => new { uuid, nickname = uuid == Viewer ? "alice" : "bob", display_name = uuid == Viewer ? "Alice" : "Bob" };
    private object Detail() => new
    {
        id = 7, title = Title, description = Description, state = State, author = Actor(Viewer), updated_on = "2026-09-10T12:00:00Z",
        source = new { branch = new { name = "feature" }, commit = new { hash = CurrentHead }, repository = new { full_name = "team/repo" } },
        destination = new { branch = new { name = "main" }, commit = new { hash = CurrentBase }, repository = new { full_name = ForeignRepository ? "other/repo" : "team/repo" } },
        reviewers = new[] { Actor(Reviewer) }
    };
    private object Comment(int id = 41) => new { id, content = new { raw = CommentBody }, user = Actor(ForeignComment ? Reviewer : Viewer),
        inline = new { path = "src/App.cs", to = 3 }, created_on = "2026-09-10T12:00:00Z", deleted = false };
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Override?.Invoke(request) is { } overridden) return overridden;
        var uri = request.RequestUri!; var path = uri.AbsolutePath["/2.0/".Length..];
        var method = request.Method.Method;
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        if (method != "GET")
        {
            Writes.Enqueue((method, path, body)); AfterWrite?.Invoke(Writes.Count);
            if (FailWrite == Writes.Count) return Json(new { error = "must-not-leak-secret" }, HttpStatusCode.ServiceUnavailable);
            if (InvalidAcknowledgement) return Json(new { });
            if (path == "repositories/team/repo") return Json(new { full_name = "team/repo", mainbranch = new { name = "main" } });
            if (path.EndsWith("/merge", StringComparison.Ordinal)) { State = "MERGED"; return Json(Detail()); }
            if (path.EndsWith("/decline", StringComparison.Ordinal)) { State = "DECLINED"; return Json(Detail()); }
            if (path.EndsWith("/approve", StringComparison.Ordinal) || path.EndsWith("/request-changes", StringComparison.Ordinal)) return Json(new { user = Actor(Viewer), approved = true });
            if (method == "DELETE" || path.EndsWith("/resolve", StringComparison.Ordinal)) return new(HttpStatusCode.NoContent);
            if (path.EndsWith("/pullrequests", StringComparison.Ordinal)) return Json(Detail());
            if (path == "repositories/team/repo/pullrequests/7" && body is not null)
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("title", out var title)) Title = title.GetString()!;
                if (document.RootElement.TryGetProperty("description", out var description)) Description = description.GetString()!;
                return Json(Detail());
            }
            return Json(Comment());
        }
        Reads.Enqueue(uri.PathAndQuery);
        if (path == "user") return Json(Actor(CurrentViewer));
        if (path == "repositories/team/repo") return Json(new { full_name = "team/repo", mainbranch = new { name = "main" } });
        if (path.EndsWith("/pullrequests", StringComparison.Ordinal)) return Json(new { values = new[] { Detail() } });
        if (path == "repositories/team/repo/pullrequests/7") { DetailReads++; OnDetailRead?.Invoke(DetailReads); return Json(Detail()); }
        if (path.EndsWith("/diffstat", StringComparison.Ordinal)) return Json(new { values = new[] { new { status = Renamed ? "renamed" : "modified", old = new { path = Renamed ? "src/Old.cs" : "src/App.cs" }, @new = new { path = "src/App.cs" }, lines_added = 1, lines_removed = 1 } } });
        if (path.EndsWith("/diff", StringComparison.Ordinal)) return MissingDiff ? Json(new { }, HttpStatusCode.NotFound) : new(HttpStatusCode.OK)
        {
            Content = new StringContent($"diff --git a/{(Renamed ? "src/Old.cs" : "src/App.cs")} b/src/App.cs\n--- a/{(Renamed ? "src/Old.cs" : "src/App.cs")}\n+++ b/src/App.cs\n@@ -2 +3 @@\n-old\n+new\n", Encoding.UTF8, "text/plain")
        };
        if (path.EndsWith("/commits", StringComparison.Ordinal)) return Json(new { values = new[] { new { hash = Head, message = "Commit", author = new { user = Actor(Viewer) }, date = "2026-09-10T12:00:00Z" } } });
        if (path.EndsWith("/statuses", StringComparison.Ordinal)) return Json(new { values = new[] { new { name = "CI", state = "SUCCESSFUL", key = "ci" } } });
        if (path.EndsWith("/comments", StringComparison.Ordinal)) return Json(new { values = new[] { Comment(uri.Query.Contains("page=2", StringComparison.Ordinal) ? 42 : 41) },
            next = MoreComments && !uri.Query.Contains("page=2", StringComparison.Ordinal) ? "https://api.bitbucket.org/2.0/" + path + "?pagelen=100&page=2" : null });
        if (path.Contains("/comments/", StringComparison.Ordinal)) return Json(Comment(int.Parse(path.Split('/')[^1], System.Globalization.CultureInfo.InvariantCulture)));
        throw new InvalidOperationException("Unknown Bitbucket fixture path: " + path);
    }
    private static HttpResponseMessage Json(object value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
}
