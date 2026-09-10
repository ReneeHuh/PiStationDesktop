using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using PiStation.Host.Errors;
using PiStation.Host.Projects;
using PiStation.Host.Workspaces;
using PiStation.Protocol.Errors;
using PiStation.Protocol.Models;

namespace PiStation.Host.SourceControl;

public sealed partial class SourceControlHostingService(
    ThreadWorkspaceResolver resolver,
    ProjectService projects,
    Func<string, IReadOnlyList<string>, string, string?, CancellationToken, Task<(int ExitCode, string StandardOutput, string StandardError)>>? reviewCommandExecutor = null,
    ISourceControlTextGenerator? textGenerator = null,
    SourceControlWritingSettingsStore? writingSettings = null) : IDisposable
{
    private const int MaximumStandardErrorCharacters = 64 * 1024;
    private const int MaximumStandardOutputCharacters = 2 * 1024 * 1024;
    private static readonly TimeSpan LocalTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromMinutes(5);
    private readonly ThreadWorkspaceResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    private readonly ProjectService _projects = projects ?? throw new ArgumentNullException(nameof(projects));
    private readonly Func<string, IReadOnlyList<string>, string, string?, CancellationToken, Task<(int ExitCode, string StandardOutput, string StandardError)>>? _reviewCommandExecutor = reviewCommandExecutor;

    public async Task<SourceControlRepository> DetectAsync(
        DetectSourceControlRequest request,
        CancellationToken cancellationToken = default)
    {
        var workspace = await _resolver.ResolveAsync(
            request.Target.ProjectId, request.Target.ThreadId, cancellationToken).ConfigureAwait(false);
        var remote = await RunAsync("git", ["remote", "get-url", "origin"], workspace.WorkspaceRoot, LocalTimeout, cancellationToken)
            .ConfigureAwait(false);
        EnsureSucceeded(remote, "This repository does not have an origin remote.");
        return await DescribeRemoteAsync(workspace.WorkspaceRoot, remote.StandardOutput.Trim(), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ListPullRequestsResult> ListPullRequestsAsync(
        ListPullRequestsRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.Offset < 0 || request.Offset > int.MaxValue - 101 || request.Offset % 100 != 0 ||
            request.SourceBranch is { } branch && (string.IsNullOrWhiteSpace(branch) || branch.Length > 1024 || branch.Any(char.IsControl)))
            throw new ArgumentException("Use a valid pull request page and source branch.");
        var workspace = await _resolver.ResolveAsync(
            request.Target.ProjectId, request.Target.ThreadId, cancellationToken).ConfigureAwait(false);
        var repository = await DetectAsync(new DetectSourceControlRequest(request.Target), cancellationToken)
            .ConfigureAwait(false);
        string? viewer = null;
        if (repository.Provider == SourceControlProvider.GitHub && request.Filters is { } filters &&
            (filters.Involvement != PullRequestInvolvement.All || filters.Author?.Trim() == "@me"))
        {
            using var identity = await ExecuteGraphQlAsync(repository, workspace.WorkspaceRoot, "query { viewer { login } }", new { }, cancellationToken).ConfigureAwait(false);
            viewer = NestedText(identity.RootElement, "data", "viewer", "login");
        }
        var (fileName, arguments) = BuildFilteredListCommand(repository, request, viewer);
        var result = repository.Provider == SourceControlProvider.GitHub && _reviewCommandExecutor is not null
            ? await RunReviewCommandAsync(arguments, workspace.WorkspaceRoot, null, cancellationToken).ConfigureAwait(false)
            : await RunAsync(fileName, arguments, workspace.WorkspaceRoot, NetworkTimeout, cancellationToken).ConfigureAwait(false);
        EnsureProviderSucceeded(result, repository.Provider);
        var parsed = ParsePullRequests(repository, result.StandardOutput);
        var page = repository.Provider == SourceControlProvider.GitHub ? parsed.Skip(request.Offset).ToArray() : parsed;
        var selected = page.Take(100).ToArray();
        if (repository.Provider == SourceControlProvider.GitHub && selected.Length > 0)
            selected = await ReadListCheckStatesAsync(repository, workspace.WorkspaceRoot, selected, cancellationToken).ConfigureAwait(false);
        return new ListPullRequestsResult(repository, selected,
            (repository.Provider == SourceControlProvider.GitHub ? page.Length > 100 : page.Length == 100) ? request.Offset + 100 : null,
            repository.Provider == SourceControlProvider.GitHub && arguments.Contains("--search") && parsed.Length >= 1000
                ? "GitHub search returns up to 1000 results. Narrow the filters to find additional pull requests." : null);
    }

    public async Task<PullRequestDescriptor> GetPullRequestAsync(WorkspaceTarget target, string number, CancellationToken cancellationToken = default)
    {
        if (!long.TryParse(number, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new ArgumentException("A positive pull request number is required.");
        var workspace = await _resolver.ResolveAsync(target.ProjectId, target.ThreadId, cancellationToken).ConfigureAwait(false);
        var repository = await DetectAsync(new(target), cancellationToken).ConfigureAwait(false);
        var (tool, arguments) = repository.Provider switch
        {
            SourceControlProvider.GitHub => ("gh", new[] { "pr", "view", number, "--json", "number,title,url,state,author,headRefName,baseRefName,isDraft,labels,reviewRequests,statusCheckRollup,updatedAt,closedAt,mergedAt" }),
            SourceControlProvider.GitLab => ("glab", new[] { "mr", "view", number, "--output", "json" }),
            SourceControlProvider.AzureDevOps => ("az", new[] { "repos", "pr", "show", "--id", number, "--output", "json" }),
            _ => throw UnsupportedProvider(repository.Provider),
        };
        var result = await RunHostingCommandAsync(tool, arguments, workspace.WorkspaceRoot, cancellationToken).ConfigureAwait(false);
        EnsureProviderSucceeded(result, repository.Provider);
        using var document = JsonDocument.Parse(result.StandardOutput);
        return ParsePullRequest(repository, document.RootElement);
    }

    public async Task<SourceControlOperationResult> CloneAsync(
        CloneHostedRepositoryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RemoteUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);
        var destination = Path.GetFullPath(request.DestinationPath);
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            throw new HostOperationException(
                ProtocolErrorCodes.GitConflict,
                "The clone destination already exists and is not empty.");
        }

        var parent = Directory.GetParent(destination)?.FullName
            ?? throw new HostOperationException(ProtocolErrorCodes.GitInvalid, "The clone destination has no parent directory.");
        Directory.CreateDirectory(parent);
        var result = await RunAsync(
            "git", ["clone", "--", request.RemoteUrl.Trim(), destination], parent, NetworkTimeout, cancellationToken)
            .ConfigureAwait(false);
        EnsureSucceeded(result, "The repository could not be cloned.");
        var project = await _projects.AddAsync(
            new AddProjectRequest(destination, request.DisplayName), cancellationToken).ConfigureAwait(false);
        var repository = await DescribeRemoteAsync(destination, request.RemoteUrl.Trim(), cancellationToken).ConfigureAwait(false);
        return new SourceControlOperationResult(true, "Repository cloned and added as a project.", repository, Project: project);
    }

    public async Task<SourceControlOperationResult> CreatePullRequestAsync(
        CreatePullRequestRequest request,
        CancellationToken cancellationToken = default)
    {
        var workspace = await _resolver.ResolveAsync(
            request.Target.ProjectId, request.Target.ThreadId, cancellationToken).ConfigureAwait(false);
        var repository = await DetectAsync(new DetectSourceControlRequest(request.Target), cancellationToken)
            .ConfigureAwait(false);
        if (!repository.CanWrite || !HostingCapabilities.CanCreate(repository.Provider)) throw UnsupportedProvider(repository.Provider);
        var (fileName, arguments) = BuildCreateCommand(repository.Provider, request);
        var result = await RunHostingCommandAsync(fileName, arguments, workspace.WorkspaceRoot, cancellationToken)
            .ConfigureAwait(false);
        EnsureProviderSucceeded(result, repository.Provider);
        return new SourceControlOperationResult(true,
            string.IsNullOrWhiteSpace(result.StandardOutput) ? "Pull request created. Refresh to inspect it." : result.StandardOutput.Trim(), repository);
    }

    public async Task<SourceControlOperationResult> MutatePullRequestAsync(
        MutatePullRequestRequest request,
        CancellationToken cancellationToken = default)
    {
        var workspace = await _resolver.ResolveAsync(
            request.Target.ProjectId, request.Target.ThreadId, cancellationToken).ConfigureAwait(false);
        var repository = await DetectAsync(new DetectSourceControlRequest(request.Target), cancellationToken)
            .ConfigureAwait(false);
        if (!repository.CanWrite || !HostingCapabilities.CanMutate(repository.Provider, request.Mutation)) throw UnsupportedMutation(request.Mutation);
        var (fileName, arguments) = BuildMutationCommand(repository.Provider, request);
        var result = await RunHostingCommandAsync(fileName, arguments, workspace.WorkspaceRoot, cancellationToken)
            .ConfigureAwait(false);
        EnsureProviderSucceeded(result, repository.Provider);
        return new SourceControlOperationResult(true,
            string.IsNullOrWhiteSpace(result.StandardOutput) ? $"Pull request {request.Mutation} completed." : result.StandardOutput.Trim(), repository);
    }

    public static async Task<IReadOnlyList<RuntimeDiagnostic>> GetToolDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<RuntimeDiagnostic>();
        foreach (var (provider, tool) in new[]
                 {
                     (SourceControlProvider.GitHub, "gh"),
                     (SourceControlProvider.GitLab, "glab"),
                     (SourceControlProvider.AzureDevOps, "az"),
                 })
        {
            var result = await RunAsync(tool, ["--version"], Environment.CurrentDirectory, LocalTimeout, cancellationToken, false)
                .ConfigureAwait(false);
            diagnostics.Add(new RuntimeDiagnostic(
                provider.ToString(),
                result.ExitCode == 0 ? "Installed · authentication not checked" : "Unavailable",
                result.ExitCode == 0 ? result.StandardOutput.Split('\n')[0].Trim() : $"Install and authenticate the '{tool}' CLI."));
        }
        return diagnostics;
    }

    internal static (string FileName, string[] Arguments) BuildListCommand(
        SourceControlProvider provider,
        PullRequestState? state, int offset = 0, string? sourceBranch = null) => provider switch
    {
        SourceControlProvider.GitHub => ("gh", Compact(["pr", "list", "--state", StateArgument(state), "--limit", (offset + 101).ToString(System.Globalization.CultureInfo.InvariantCulture), "--json", "number,title,url,state,author,headRefName,baseRefName,isDraft,labels,reviewRequests,updatedAt,closedAt,mergedAt", sourceBranch is null ? null : "--head", sourceBranch])),
        SourceControlProvider.GitLab => ("glab", Compact(["mr", "list", state switch { PullRequestState.Closed => "--closed", PullRequestState.Merged => "--merged", PullRequestState.Draft => "--draft", null => "--all", _ => null }, "--per-page", "100", "--page", (offset / 100 + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "--output", "json", sourceBranch is null ? null : "--source-branch", sourceBranch])),
        SourceControlProvider.Bitbucket => throw UnsupportedProvider(provider),
        SourceControlProvider.AzureDevOps => ("az", Compact(["repos", "pr", "list", "--status", AzureStateArgument(state), "--top", "100", "--skip", offset.ToString(System.Globalization.CultureInfo.InvariantCulture), "--output", "json", sourceBranch is null ? null : "--source-branch", sourceBranch])),
        _ => throw UnsupportedProvider(provider),
    };

    internal static (string FileName, string[] Arguments) BuildCreateCommand(
        SourceControlProvider provider,
        CreatePullRequestRequest request)
    {
        var source = request.SourceBranch;
        var target = request.TargetBranch;
        return provider switch
        {
            SourceControlProvider.GitHub => ("gh", Compact(["pr", "create", "--title", request.Title, "--body", request.Body, request.IsDraft ? "--draft" : null, source is null ? null : "--head", source, target is null ? null : "--base", target])),
            SourceControlProvider.GitLab => ("glab", Compact(["mr", "create", "--title", request.Title, "--description", request.Body, request.IsDraft ? "--draft" : null, source is null ? null : "--source-branch", source, target is null ? null : "--target-branch", target, "--yes"])),
            SourceControlProvider.Bitbucket => throw UnsupportedProvider(provider),
            SourceControlProvider.AzureDevOps => ("az", Compact(["repos", "pr", "create", "--title", request.Title, "--description", request.Body, source is null ? null : "--source-branch", source, target is null ? null : "--target-branch", target, "--draft", request.IsDraft ? "true" : "false", "--output", "json"])),
            _ => throw UnsupportedProvider(provider),
        };
    }

    internal static (string FileName, string[] Arguments) BuildMutationCommand(
        SourceControlProvider provider,
        MutatePullRequestRequest request) => provider switch
    {
        SourceControlProvider.GitHub => ("gh", request.Mutation switch
        {
            PullRequestMutationKind.Comment => ["pr", "comment", request.Number, "--body", RequiredValue(request)],
            PullRequestMutationKind.AddLabel => ["pr", "edit", request.Number, "--add-label", RequiredValue(request)],
            PullRequestMutationKind.AddReviewer => ["pr", "edit", request.Number, "--add-reviewer", RequiredValue(request)],
            PullRequestMutationKind.Approve => ["pr", "review", request.Number, "--approve", "--body", request.Value ?? string.Empty],
            PullRequestMutationKind.RequestChanges => ["pr", "review", request.Number, "--request-changes", "--body", RequiredValue(request)],
            PullRequestMutationKind.Merge => ["pr", "merge", request.Number, "--merge"],
            PullRequestMutationKind.Close => ["pr", "close", request.Number],
            PullRequestMutationKind.Reopen => ["pr", "reopen", request.Number],
            _ => throw UnsupportedMutation(request.Mutation),
        }),
        SourceControlProvider.GitLab => ("glab", request.Mutation switch
        {
            PullRequestMutationKind.Comment => ["mr", "note", request.Number, "--message", RequiredValue(request)],
            PullRequestMutationKind.AddLabel => ["mr", "update", request.Number, "--label", RequiredValue(request)],
            PullRequestMutationKind.AddReviewer => ["mr", "update", request.Number, "--reviewer", RequiredValue(request)],
            PullRequestMutationKind.Approve => ["mr", "approve", request.Number],
            PullRequestMutationKind.Merge => ["mr", "merge", request.Number, "--yes"],
            PullRequestMutationKind.Close => ["mr", "close", request.Number],
            PullRequestMutationKind.Reopen => ["mr", "reopen", request.Number],
            _ => throw UnsupportedMutation(request.Mutation),
        }),
        SourceControlProvider.Bitbucket => throw UnsupportedProvider(provider),
        SourceControlProvider.AzureDevOps => ("az", BuildAzureMutation(request)),
        _ => throw UnsupportedProvider(provider),
    };

    private static string[] BuildAzureMutation(MutatePullRequestRequest request) => request.Mutation switch
    {
        PullRequestMutationKind.AddReviewer => ["repos", "pr", "reviewer", "add", "--id", request.Number, "--reviewers", RequiredValue(request)],
        PullRequestMutationKind.Approve => ["repos", "pr", "set-vote", "--id", request.Number, "--vote", "approve"],
        PullRequestMutationKind.RequestChanges => ["repos", "pr", "set-vote", "--id", request.Number, "--vote", "reject"],
        PullRequestMutationKind.Merge => ["repos", "pr", "update", "--id", request.Number, "--status", "completed"],
        PullRequestMutationKind.Close => ["repos", "pr", "update", "--id", request.Number, "--status", "abandoned"],
        PullRequestMutationKind.Reopen => ["repos", "pr", "update", "--id", request.Number, "--status", "active"],
        _ => throw UnsupportedMutation(request.Mutation),
    };

    internal static PullRequestDescriptor[] ParsePullRequests(
        SourceControlRepository repository,
        string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in new[] { "values", "items", "value", "pullRequests" })
                {
                    if (root.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array)
                    {
                        root = array;
                        break;
                    }
                }
            }
            if (root.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return root.EnumerateArray().Select(item => ParsePullRequest(repository, item)).ToArray();
        }
        catch (JsonException exception)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.SourceControlOperationFailed,
                $"The {repository.Provider} CLI returned invalid JSON: {exception.Message}");
        }
    }

    private static PullRequestDescriptor ParsePullRequest(SourceControlRepository repository, JsonElement item)
    {
        var number = Text(item, "number") ?? Text(item, "iid") ?? Text(item, "pullRequestId") ?? Text(item, "id") ?? "?";
        var rawState = Text(item, "state") ?? Text(item, "status") ?? "unknown";
        var isDraft = Boolean(item, "isDraft") || Boolean(item, "draft") || Text(item, "title")?.StartsWith("Draft:", StringComparison.OrdinalIgnoreCase) == true;
        var state = rawState.ToLowerInvariant() switch
        {
            "open" or "opened" or "active" => isDraft ? PullRequestState.Draft : PullRequestState.Open,
            "closed" or "abandoned" => PullRequestState.Closed,
            "merged" or "completed" => PullRequestState.Merged,
            "draft" => PullRequestState.Draft,
            _ => PullRequestState.Unknown,
        };
        var author = NestedText(item, "author", "login") ?? NestedText(item, "author", "username") ??
                     NestedText(item, "author", "display_name") ?? NestedText(item, "author", "displayName") ??
                     NestedText(item, "createdBy", "displayName") ?? Text(item, "author") ?? "Unknown";
        var labels = ReadNameArray(item, "labels");
        var reviewers = ReadNameArray(item, "reviewRequests");
        if (reviewers.Count == 0)
        {
            reviewers = ReadNameArray(item, "reviewers");
        }
        var checks = ReadChecks(item);
        var fallbackUrl = repository.Provider switch
        {
            SourceControlProvider.Bitbucket => $"{repository.WebUrl}/pull-requests/{number}",
            SourceControlProvider.GitLab => $"{repository.WebUrl}/-/merge_requests/{number}",
            SourceControlProvider.AzureDevOps => $"{repository.WebUrl}/pullrequest/{number}",
            _ => $"{repository.WebUrl}/pull/{number}",
        };
        var url = Text(item, "url") ?? Text(item, "web_url") ?? Text(item, "webUrl") ??
                  NestedText(item, "links", "html", "href") ?? fallbackUrl;
        var updated = DateTimeOffset.TryParse(
            Text(item, "updatedAt") ?? Text(item, "updated_at") ?? Text(item, "updated_on") ??
            Text(item, "creationDate"), out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
        var sourceBranch = NormalizeBranch(
            Text(item, "headRefName") ?? Text(item, "source_branch") ?? Text(item, "sourceRefName") ??
            NestedText(item, "source", "branch", "name"));
        var targetBranch = NormalizeBranch(
            Text(item, "baseRefName") ?? Text(item, "target_branch") ?? Text(item, "targetRefName") ??
            NestedText(item, "destination", "branch", "name")) ?? repository.DefaultBranch;
        return new PullRequestDescriptor(
            repository.Provider, $"{repository.Owner}/{repository.Name}", number,
            Text(item, "title") ?? "Untitled pull request", url, state, author,
            sourceBranch ?? string.Empty,
            targetBranch,
            isDraft, labels, reviewers, checks, updated,
            DateTimeOffset.TryParse(Text(item, "mergedAt") ?? Text(item, "merged_at") ?? Text(item, "closedAt") ?? Text(item, "closed_at") ?? Text(item, "closedDate"),
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var closedAt) ? closedAt : null);
    }

    private static PullRequestCheckState ReadChecks(JsonElement item)
    {
        if (!item.TryGetProperty("statusCheckRollup", out var checks) || checks.ValueKind != JsonValueKind.Array)
        {
            return PullRequestCheckState.Unknown;
        }
        var states = checks.EnumerateArray().Select(check =>
            (Text(check, "conclusion") ?? Text(check, "state") ?? Text(check, "status") ?? string.Empty).ToLowerInvariant()).ToArray();
        return states.Any(state => state is "failure" or "failed" or "error" or "cancelled")
            ? PullRequestCheckState.Failed
            : states.Any(state => state is "pending" or "queued" or "in_progress")
                ? PullRequestCheckState.Pending
                : states.Length > 0 ? PullRequestCheckState.Passed : PullRequestCheckState.Unknown;
    }

    private static List<string> ReadNameArray(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return array.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : Text(value, "name") ?? Text(value, "login") ?? Text(value, "username") ?? Text(value, "displayName"))
            .Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value!).ToList();
    }

    private async Task<SourceControlRepository> DescribeRemoteAsync(
        string workspace,
        string remote,
        CancellationToken cancellationToken)
    {
        var parsed = ParseRemote(remote);
        var defaultBranchResult = await RunAsync(
            "git", ["symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"], workspace, LocalTimeout, cancellationToken)
            .ConfigureAwait(false);
        var defaultBranch = defaultBranchResult.ExitCode == 0
            ? defaultBranchResult.StandardOutput.Trim().Replace("origin/", string.Empty, StringComparison.Ordinal)
            : "main";
        var tool = ToolFor(parsed.Provider);
        var authArguments = parsed.Provider switch
        {
            SourceControlProvider.GitHub => new[] { "auth", "status", "--active", "--hostname", parsed.Host },
            SourceControlProvider.GitLab => ["auth", "status", "--hostname", parsed.Host],
            SourceControlProvider.AzureDevOps => ["repos", "show", "--repository", parsed.Name, "--output", "none"],
            _ => [],
        };
        var available = authArguments.Length != 0 && (parsed.Provider == SourceControlProvider.GitHub && _reviewCommandExecutor is not null
            ? (await RunReviewCommandAsync(authArguments, workspace, null, cancellationToken).ConfigureAwait(false)).ExitCode == 0
            : (await RunAsync(tool, authArguments, workspace, LocalTimeout, cancellationToken, false).ConfigureAwait(false)).ExitCode == 0);
        return new SourceControlRepository(
            parsed.Provider, parsed.Host, parsed.Owner, parsed.Name,
            parsed.WebUrl, remote, defaultBranch, available);
    }

    internal static (SourceControlProvider Provider, string Host, string Owner, string Name, string WebUrl) ParseRemote(string remote)
    {
        var normalized = remote.Trim();
        if (normalized.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            var separator = normalized.IndexOf(':');
            normalized = separator > 4
                ? $"https://{normalized[4..separator]}/{normalized[(separator + 1)..]}"
                : normalized;
        }
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            throw new HostOperationException(ProtocolErrorCodes.SourceControlUnavailable, "The origin remote URL is not supported.");
        }
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (segments.Count < 2)
        {
            throw new HostOperationException(ProtocolErrorCodes.SourceControlUnavailable, "The origin remote does not identify a repository.");
        }
        var provider = uri.Host.Contains("github", StringComparison.OrdinalIgnoreCase) ? SourceControlProvider.GitHub
            : uri.Host.Contains("gitlab", StringComparison.OrdinalIgnoreCase) ? SourceControlProvider.GitLab
            : uri.Host.Contains("bitbucket", StringComparison.OrdinalIgnoreCase) ? SourceControlProvider.Bitbucket
            : uri.Host.Contains("dev.azure", StringComparison.OrdinalIgnoreCase) || uri.Host.Contains("visualstudio", StringComparison.OrdinalIgnoreCase) ? SourceControlProvider.AzureDevOps
            : SourceControlProvider.Unknown;
        var webSegments = segments.ToArray();
        if (provider == SourceControlProvider.AzureDevOps && segments.Contains("_git"))
        {
            var marker = segments.IndexOf("_git");
            segments = [segments[Math.Max(0, marker - 1)], segments[Math.Min(segments.Count - 1, marker + 1)]];
        }
        var owner = provider == SourceControlProvider.GitLab ? string.Join('/', segments.Take(segments.Count - 1)) : segments[^2];
        var name = segments[^1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[^1][..^4] : segments[^1];
        if (webSegments[^1].EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            webSegments[^1] = webSegments[^1][..^4];
        }
        var web = $"{uri.Scheme}://{uri.Host}/{string.Join('/', webSegments)}";
        return (provider, uri.Host, owner, name, web);
    }

    private static async Task<string> CurrentBranchAsync(string workspace, CancellationToken cancellationToken)
    {
        var result = await RunAsync("git", ["branch", "--show-current"], workspace, LocalTimeout, cancellationToken)
            .ConfigureAwait(false);
        EnsureSucceeded(result, "The current branch could not be determined.");
        return result.StandardOutput.Trim();
    }

    private static string StateArgument(PullRequestState? state) => state switch
    {
        null => "all",
        PullRequestState.Closed => "closed",
        PullRequestState.Merged => "merged",
        _ => "open",
    };

    private static string AzureStateArgument(PullRequestState? state) => state switch
    {
        PullRequestState.Closed => "abandoned",
        PullRequestState.Merged => "completed",
        _ => "active",
    };

    private static string ToolFor(SourceControlProvider provider) => provider switch
    {
        SourceControlProvider.GitHub => "gh",
        SourceControlProvider.GitLab => "glab",
        SourceControlProvider.Bitbucket => "bb",
        SourceControlProvider.AzureDevOps => "az",
        _ => "git",
    };

    private static string[] Compact(IEnumerable<string?> values) => values.Where(static value => value is not null).Select(static value => value!).ToArray();

    private static string RequiredValue(MutatePullRequestRequest request) =>
        string.IsNullOrWhiteSpace(request.Value)
            ? throw new HostOperationException(ProtocolErrorCodes.SourceControlOperationFailed, $"{request.Mutation} requires a value.")
            : request.Value;

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) ? value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        } : null;

    private static string? NestedText(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var part in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
            {
                return null;
            }
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string? NormalizeBranch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.StartsWith("refs/heads/", StringComparison.OrdinalIgnoreCase)
            ? value["refs/heads/".Length..]
            : value;
    }

    private static bool Boolean(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    private static void EnsureProviderSucceeded(ProcessResult result, SourceControlProvider provider)
    {
        if (result.ExitCode == 0)
        {
            return;
        }
        var detail = string.IsNullOrWhiteSpace(result.StandardError) ? result.StandardOutput : result.StandardError;
        var code = detail.Contains("auth", StringComparison.OrdinalIgnoreCase) ||
                   detail.Contains("login", StringComparison.OrdinalIgnoreCase)
            ? ProtocolErrorCodes.SourceControlAuthenticationRequired
            : ProtocolErrorCodes.SourceControlOperationFailed;
        throw new HostOperationException(code, $"{provider} operation failed. {detail.Trim()}".Trim());
    }

    private static void EnsureSucceeded(ProcessResult result, string message)
    {
        if (result.ExitCode != 0)
        {
            throw new HostOperationException(
                ProtocolErrorCodes.SourceControlUnavailable,
                $"{message} {result.StandardError.Trim()}".Trim());
        }
    }

    private static HostOperationException UnsupportedProvider(SourceControlProvider provider) => new(
        ProtocolErrorCodes.SourceControlUnavailable,
        $"No source-control hosting adapter is available for '{provider}'.");

    private static HostOperationException UnsupportedMutation(PullRequestMutationKind mutation) => new(
        ProtocolErrorCodes.SourceControlOperationFailed,
        $"The selected provider does not support '{mutation}' through its CLI.");

    private async Task<ProcessResult> RunHostingCommandAsync(string tool, IReadOnlyList<string> arguments, string workspace, CancellationToken cancellationToken)
    {
        if (_reviewCommandExecutor is null) return await RunAsync(tool, arguments, workspace, NetworkTimeout, cancellationToken).ConfigureAwait(false);
        var result = await _reviewCommandExecutor(tool, arguments, workspace, null, cancellationToken).ConfigureAwait(false);
        return new(result.ExitCode, result.StandardOutput, result.StandardError);
    }

    private static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool throwWhenMissing = true,
        string? standardInput = null,
        IReadOnlyDictionary<string, string?>? environmentVariables = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = HostingCliLocator.Resolve(fileName),
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (standardInput is not null)
            startInfo.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GH_PAGER"] = "cat";
        startInfo.Environment["GLAB_PAGER"] = "cat";
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (environmentVariables is not null)
            foreach (var variable in environmentVariables) startInfo.Environment[variable.Key] = variable.Value;
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new ProcessResult(-1, string.Empty, $"'{fileName}' could not be started.");
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            if (throwWhenMissing)
            {
                throw new HostOperationException(
                    ProtocolErrorCodes.SourceControlUnavailable,
                    $"The '{fileName}' CLI is not installed or could not be started.");
            }
            return new ProcessResult(-1, string.Empty, exception.Message);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            var stdout = ReadBoundedAsync(process.StandardOutput, MaximumStandardOutputCharacters, timeoutSource.Token);
            var stderr = ReadBoundedAsync(process.StandardError, MaximumStandardErrorCharacters, timeoutSource.Token);
            if (standardInput is not null)
            {
                await process.StandardInput.WriteAsync(standardInput.AsMemory(), timeoutSource.Token).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return new ProcessResult(
                process.ExitCode,
                await stdout.ConfigureAwait(false),
                await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new HostOperationException(ProtocolErrorCodes.SourceControlUnavailable, $"'{fileName}' timed out.");
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maximumCharacters, 16 * 1024));
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return builder.ToString();
            }

            var remaining = maximumCharacters - builder.Length;
            if (remaining > 0)
            {
                builder.Append(buffer, 0, Math.Min(read, remaining));
            }
        }
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
