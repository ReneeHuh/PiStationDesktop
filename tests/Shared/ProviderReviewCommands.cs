using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using PiStation.Protocol.Models;

namespace PiStation.TestFixtures;

// Provider traffic is entirely simulated. Git is confined to a temporary local repository.
internal sealed class ProviderReviewCommands(SourceControlProvider provider)
{
    private static readonly string[] Labels = ["bug"];
    public const string Head = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string Base = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    public const string RepositoryId = "f13c86ac-5156-4d7c-b2a0-b945af2a4922";
    public string CurrentHead { get; set; } = Head;
    public string CurrentBase { get; set; } = Base;
    public string Title { get; set; } = "Review fixture";
    public string Description { get; set; } = "Description";
    public bool CanMerge { get; set; } = true;
    public bool AutoMerge { get; set; }
    public bool Draft { get; set; }
    public bool MissingViewer { get; set; }
    public bool ForeignRepository { get; set; }
    public bool WrongThread { get; set; }
    public bool ForeignComment { get; set; }
    public bool FailedDiscussionRead { get; set; }
    public bool FailedFilesRead { get; set; }
    public bool ContextDiff { get; set; }
    public bool Renamed { get; set; }
    public bool Collapsed { get; set; }
    public bool Resolved { get; set; }
    public bool ReactionEnabled { get; set; }
    public bool TruncatedAwards { get; set; }
    public bool EndlessAwardPages { get; set; }
    public int AwardReads { get; private set; }
    public int FileCount { get; set; } = 1;
    public int FailedWrite { get; set; }
    public bool InvalidWriteResponse { get; set; }
    public int DetailReads { get; private set; }
    public string MergeMethod { get; set; } = "merge";
    public string SquashOption { get; set; } = "default_off";
    public Action<int>? OnDetailRead { get; set; }
    public Action<int>? OnWrite { get; set; }
    public ConcurrentQueue<(string Tool, string[] Arguments, string? Input)> Calls { get; } = new();
    public ConcurrentQueue<(string Path, string? Input)> Writes { get; } = new();
    public string WebUrl => provider == SourceControlProvider.GitLab ? "https://gitlab.example/team/subgroup/repo" : "https://dev.azure.com/station/Team%20Project/_git/repo";
    public string RemoteUrl => provider == SourceControlProvider.GitLab ? WebUrl + ".git" : WebUrl;

    public Task<(int, string, string)> RunAsync(string tool, IReadOnlyList<string> arguments, string workspace, string? input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var args = arguments.ToArray();
        if (tool == "az" && args.Contains("--in-file")) input = File.ReadAllText(args[Array.IndexOf(args, "--in-file") + 1]);
        Calls.Enqueue((tool, args, input));
        if (args.Contains("auth") || tool == "az" && args.Contains("show") && !args.Contains("pr")) return Result(new { });
        var methodFlag = tool == "az" ? "--http-method" : "--method";
        var method = args.Contains(methodFlag) ? args[Array.IndexOf(args, methodFlag) + 1] : "GET";
        var path = tool == "glab" ? args[^1] : args.Contains("--resource") ? args[Array.IndexOf(args, "--resource") + 1] : string.Join(' ', args.Take(4));
        if (path == "graphql")
        {
            AwardReads++;
            object[] awards = ReactionEnabled ? [new { name = "thumbsup", user = new { username = "alice" } }] : [];
            object[] notes = EndlessAwardPages ? [] : [new { id = "gid://gitlab/Note/41", awardEmoji = new { pageInfo = new { hasNextPage = false }, nodes = awards } }];
            return Result(new { data = new { currentUser = new { username = MissingViewer ? "" : "alice" }, project = new { mergeRequest = new
            {
                awardEmoji = new { pageInfo = new { hasNextPage = TruncatedAwards }, nodes = awards },
                notes = new { pageInfo = new { hasNextPage = EndlessAwardPages, endCursor = AwardReads.ToString(CultureInfo.InvariantCulture) }, nodes = notes }
            } } } });
        }
        var write = method != "GET" || tool == "az" && (args.Contains("update") || args.Contains("remove"));
        if (write)
        {
            Writes.Enqueue((path, input));
            OnWrite?.Invoke(Writes.Count);
            if (Writes.Count == FailedWrite) return Task.FromResult((1, "", "secret fixture credential must not escape"));
            if (InvalidWriteResponse) return Task.FromResult((0, "not JSON", ""));
            return Result(new { id = 100, rebase_in_progress = true, pullRequestId = 7 });
        }
        if (tool == "az")
        {
            if (path == "pullRequestThreads") return FailedDiscussionRead ? Failure() : Result(new { value = new[] { new { id = 1, status = "active", comments = new[] { new { id = 1, content = "Please check", author = new { displayName = "Alice" }, publishedDate = "2026-09-10T12:00:00Z" } } } } });
            DetailReads++;
            OnDetailRead?.Invoke(DetailReads);
            return Result(new { pullRequestId = 7, title = Title, description = Description, status = "active", isDraft = Draft,
                repository = new { id = RepositoryId, name = ForeignRepository ? "other" : "repo", project = new { name = "Team Project" } },
                lastMergeSourceCommit = new { commitId = CurrentHead }, lastMergeTargetCommit = new { commitId = CurrentBase },
                sourceRefName = "refs/heads/feature", targetRefName = "refs/heads/main", createdBy = new { displayName = "Alice" }, creationDate = "2026-09-10T12:00:00Z",
                mergeStatus = "succeeded", autoCompleteSetBy = AutoMerge ? new { id = RepositoryId } : null,
                reviewers = new[] { new { id = RepositoryId, displayName = "Alice", uniqueName = "alice@example.invalid" } } });
        }
        if (path == "user") return MissingViewer ? Failure() : Result(new { username = "alice" });
        if (path == "projects/team%2Fsubgroup%2Frepo") return Result(new { id = 17, merge_method = MergeMethod, squash_option = SquashOption });
        if (path.Contains("award_emoji", StringComparison.Ordinal)) return Result(ReactionEnabled ? new[] { new { id = 77, name = "thumbsup", user = new { username = "alice" } } } : Array.Empty<object>());
        if (path.Contains("/diffs?", StringComparison.Ordinal))
        {
            if (FailedFilesRead) return Failure();
            var page = int.Parse(path[(path.LastIndexOf("page=", StringComparison.Ordinal) + 5)..], CultureInfo.InvariantCulture);
            return Result(Enumerable.Range(0, FileCount).Skip((page - 1) * 100).Take(100).Select(i => new { new_path = i == 0 ? "src/App.cs" : $"src/File{i}.cs",
                old_path = Renamed && i == 0 ? "src/Old.cs" : i == 0 ? "src/App.cs" : $"src/File{i}.cs", renamed_file = Renamed,
                diff = ContextDiff ? "@@ -2 +3 @@\n context\n" : "@@ -2 +3 @@\n-old\n+new\n", collapsed = Collapsed }).ToArray());
        }
        if (path.Contains("/commits?", StringComparison.Ordinal)) return Result(new[] { new { id = CurrentHead, title = "Commit", author_name = "Alice", created_at = "2026-09-10T12:00:00Z" } });
        if (path.Contains("/pipelines?", StringComparison.Ordinal)) return Result(new[] { new { id = 8, sha = CurrentHead, status = "success", web_url = WebUrl + "/pipelines/8" }, new { id = 9, sha = "old", status = "failed", web_url = "" } });
        if (path.Contains("/discussions", StringComparison.Ordinal))
        {
            if (FailedDiscussionRead) return Failure();
            var thread = new { id = WrongThread ? "another" : "thread1", individual_note = false, notes = new[] { Note() } };
            return path.Contains('?', StringComparison.Ordinal) ? Result(new[] { thread }) : Result(thread);
        }
        if (path.Contains("/notes/", StringComparison.Ordinal)) return Result(Note());
        if (path.EndsWith("/merge_requests/7", StringComparison.Ordinal))
        {
            DetailReads++;
            OnDetailRead?.Invoke(DetailReads);
            return Result(new { iid = 7, project_id = 17, title = Title, description = Description, state = "opened", draft = Draft, sha = CurrentHead,
                diff_refs = new { head_sha = CurrentHead, base_sha = CurrentBase, start_sha = Base }, web_url = (ForeignRepository ? "https://gitlab.example/other/repo" : WebUrl) + "/-/merge_requests/7",
                source_branch = "feature", target_branch = "main", author = new { username = "alice" }, updated_at = "2026-09-10T12:00:00Z", labels = Labels,
                reviewers = new[] { new { id = 12, username = "alice", name = "Alice" }, new { id = 13, username = "bob", name = "Bob" } },
                user = new { can_merge = CanMerge }, detailed_merge_status = "mergeable", merge_when_pipeline_succeeds = AutoMerge, diverged_commits_count = 0 });
        }
        throw new InvalidOperationException("Unrecognized fixture request: " + path);
    }

    private object Note() => new { id = 41, body = "Please check", author = new { username = ForeignComment ? "bob" : "alice" }, created_at = "2026-09-10T12:00:00Z",
        resolvable = true, resolved = Resolved, system = false, position = new { new_path = "src/App.cs", new_line = 3, head_sha = CurrentHead } };
    private static Task<(int, string, string)> Result(object value) => Task.FromResult((0, JsonSerializer.Serialize(value), ""));
    private static Task<(int, string, string)> Failure() => Task.FromResult((1, "", "fixture unavailable"));
    public async Task InitializeRepositoryAsync(string path)
    {
        await GitAsync(path, "init", "--quiet", "--initial-branch=main");
        await GitAsync(path, "remote", "add", "origin", RemoteUrl);
    }
    public static async Task GitAsync(string path, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = path, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await output;
        if (process.ExitCode != 0) throw new InvalidOperationException(await error);
    }
}
