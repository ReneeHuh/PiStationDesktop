using System.Globalization;
using System.Text.Json;
using PiStation.Host.Errors;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;

namespace PiStation.Host.SourceControl;

public sealed partial class SourceControlHostingService
{
    private readonly SemaphoreSlim _publicationGate = new(1, 1);

    public async Task<SourceControlOperationResult> PublishAsync(
        PublishHostedRepositoryRequest request, CancellationToken cancellationToken = default)
    {
        if (!await _publicationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return Rejected("Another repository publication is running on this host. Wait for its result.", request.OperationId);
        RepositoryPublication? progress = null;
        SourceControlRepository? repository = null;
        var writeStarted = false;
        try
        {
            request = NormalizePublicationRequest(request);
            if (request.Provider == SourceControlProvider.Bitbucket && !_bitbucket.IsConfigured)
                throw new PublicationFailure("Configure Bitbucket host credentials before publishing.");
            var workspace = (await _resolver.ResolveAsync(request.ProjectId, cancellationToken: cancellationToken).ConfigureAwait(false)).WorkspaceRoot;
            var branch = (await PublishGitAsync(["symbolic-ref", "--quiet", "--short", "HEAD"], workspace, cancellationToken).ConfigureAwait(false)).Trim();
            if (string.IsNullOrEmpty(branch)) throw new PublicationFailure("Select a local branch before publishing.");
            var head = await PublicationHeadAsync(workspace, cancellationToken).ConfigureAwait(false);

            // Resolve a namespace before the first write. All provider calls name their host explicitly,
            // so another repository's origin or CLI defaults cannot redirect publication.
            string? namespaceId = null;
            if (request.Provider == SourceControlProvider.GitLab && !request.ResumeExisting)
            {
                var raw = await PublishCommandAsync("glab", ["api", "--hostname", request.Host!, "--method", "GET",
                    "namespaces/" + Uri.EscapeDataString(request.Owner)], workspace, "GitLab namespace lookup", cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(raw);
                namespaceId = Text(document.RootElement, "id");
                if (!long.TryParse(namespaceId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 ||
                    !string.Equals(Text(document.RootElement, "full_path"), request.Owner, StringComparison.OrdinalIgnoreCase))
                    throw new PublicationFailure("GitLab did not identify the requested namespace.");
            }

            writeStarted = !request.ResumeExisting;
            string output;
            if (request.Provider == SourceControlProvider.Bitbucket)
                output = await PublishBitbucketRepositoryAsync(request, cancellationToken).ConfigureAwait(false);
            else
            {
                var (tool, args) = BuildPublicationCommand(request, namespaceId);
                output = await PublishCommandAsync(tool, args, workspace,
                    request.ResumeExisting ? "Repository lookup" : "Repository creation", cancellationToken).ConfigureAwait(false);
            }
            // gh repo create prints a URL rather than JSON. Follow it with an explicit, read-only lookup.
            if (request.Provider == SourceControlProvider.GitHub && !request.ResumeExisting)
            {
                var lookup = BuildPublicationCommand(request with { ResumeExisting = true });
                output = await PublishCommandAsync(lookup.Tool, lookup.Arguments, workspace, "Created repository lookup", cancellationToken).ConfigureAwait(false);
            }
            repository = ReadPublicationRepository(request, output);
            progress = new(RepositoryPublicationStage.RepositoryReady, repository.RemoteUrl, null, branch);
            if (await PublicationHeadAsync(workspace, cancellationToken).ConfigureAwait(false) != head ||
                (await PublishGitAsync(["symbolic-ref", "--quiet", "--short", "HEAD"], workspace, cancellationToken).ConfigureAwait(false)).Trim() != branch)
                throw new PublicationFailure("The local branch changed while the repository was being prepared. Review it before resuming.");

            writeStarted = true;
            var remote = await EnsurePublicationRemoteAsync(repository.RemoteUrl, workspace, cancellationToken).ConfigureAwait(false);
            progress = progress with { Stage = RepositoryPublicationStage.RemoteConfigured, RemoteName = remote };
            if (head is null)
                return new(true, $"Repository ready at {repository.WebUrl}. Remote '{remote}' is configured; there are no commits to push yet. After committing, use Resume existing repository.",
                    repository, Publication: progress);

            // Pin the commit captured before creation. A checkout or new commit during the network
            // operation must not publish different content. Never force, mirror, or send extra tags.
            await PublishGitAsync(["-c", $"remote.{remote}.mirror=false", "push", "--porcelain", "--no-follow-tags", remote,
                $"{head}:refs/heads/{branch}"], workspace, cancellationToken).ConfigureAwait(false);
            progress = progress with { Stage = RepositoryPublicationStage.Pushed };
            await PublishGitAsync(["config", "--local", $"branch.{branch}.remote", remote], workspace, cancellationToken).ConfigureAwait(false);
            await PublishGitAsync(["config", "--local", $"branch.{branch}.merge", $"refs/heads/{branch}"], workspace, cancellationToken).ConfigureAwait(false);
            return new(true, $"Published '{branch}' at {head[..12]} to {repository.WebUrl} using remote '{remote}'. Upstream is configured.", repository, Publication: progress);
        }
        catch (Exception exception) when (exception is ArgumentException or PublicationFailure or HostOperationException or
            JsonException or IOException or InvalidOperationException or OperationCanceledException or HttpRequestException)
        {
            var reason = exception is PublicationFailure or ArgumentException ? exception.Message : "The publication step could not be confirmed. Check the host tool, authentication, and connection.";
            var recovery = progress is null
                ? writeStarted ? " Inspect the selected provider, then use Resume existing repository if it was created. Creation was not retried." : string.Empty
                : $" Repository: {repository!.WebUrl}. Last confirmed step: {progress.Stage}. Use Resume existing repository to continue; it will not create another repository.";
            return new(false, reason + recovery, repository, State: writeStarted ? CommandReceiptState.DispatchUncertain : CommandReceiptState.Rejected, Publication: progress);
        }
        finally { _publicationGate.Release(); }
    }

    internal static PublishHostedRepositoryRequest NormalizePublicationRequest(PublishHostedRepositoryRequest request)
    {
        if (!HostingCapabilities.CanPublish(request.Provider)) throw new PublicationFailure("This provider does not support publishing.");
        var owner = request.Owner?.Trim() ?? "";
        var name = request.RepositoryName?.Trim() ?? "";
        static bool Segment(string value) => value.Length is > 0 and <= 255 && value[0] != '-' && value is not "." and not ".." &&
            value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');
        if (request.Provider == SourceControlProvider.AzureDevOps)
        {
            static bool AzureName(string value) => value.Length is > 0 and <= 128 && !value.StartsWith('-') &&
                value is not "." and not ".." && !value.Any(c => char.IsControl(c) || "/\\:?*\"<>|#%".Contains(c));
            if (!AzureName(owner) || !AzureName(name)) throw new PublicationFailure("Enter a valid Azure project and repository name (up to 128 characters).");
            if (!Uri.TryCreate(request.OrganizationUrl?.Trim(), UriKind.Absolute, out var org) || org.Scheme != "https" ||
                org.UserInfo.Length != 0 || org.Query.Length != 0 || org.Fragment.Length != 0 || !org.IsDefaultPort ||
                !(org.Host == "dev.azure.com" && org.AbsolutePath.Trim('/').Split('/').Length == 1 && Segment(org.AbsolutePath.Trim('/')) ||
                  org.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase) && org.AbsolutePath.Trim('/').Length == 0))
                throw new PublicationFailure("Enter an Azure organization URL such as https://dev.azure.com/my-organization.");
            var organizationUrl = org.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase)
                ? "https://dev.azure.com/" + org.Host[..^".visualstudio.com".Length]
                : org.AbsoluteUri.TrimEnd('/');
            return request with { Owner = owner, RepositoryName = name, Host = null, OrganizationUrl = organizationUrl };
        }
        if (!Segment(name) || owner.Length > 1024 || !owner.Split('/').All(Segment) ||
            request.Provider is SourceControlProvider.GitHub or SourceControlProvider.Bitbucket && owner.Contains('/'))
            throw new PublicationFailure("Enter a valid owner or namespace and repository name. GitLab supports nested namespaces.");
        var host = string.IsNullOrWhiteSpace(request.Host) ? request.Provider switch { SourceControlProvider.GitHub => "github.com", SourceControlProvider.Bitbucket => "bitbucket.org", _ => "gitlab.com" } : request.Host.Trim();
        if (request.Provider == SourceControlProvider.Bitbucket && host != "bitbucket.org") throw new PublicationFailure("Only Bitbucket Cloud at bitbucket.org is supported.");
        if (host.Length > 255 || !Uri.TryCreate("https://" + host, UriKind.Absolute, out var hostUri) || hostUri.AbsolutePath != "/" ||
            hostUri.UserInfo.Length != 0 || hostUri.Query.Length != 0 || hostUri.Fragment.Length != 0 || host.Any(char.IsWhiteSpace))
            throw new PublicationFailure("Enter only the hosting server name, without a URL path or credentials.");
        return request with { Owner = owner, RepositoryName = name, Host = hostUri.Authority, OrganizationUrl = null };
    }

    internal static (string Tool, string[] Arguments) BuildPublicationCommand(PublishHostedRepositoryRequest request, string? namespaceId = null)
    {
        var identifier = request.Owner + "/" + request.RepositoryName;
        return request.Provider switch
        {
            SourceControlProvider.GitHub => request.ResumeExisting
                ? ("gh", ["api", "--hostname", request.Host!, "--method", "GET", "repos/" + identifier])
                : ("gh", ["repo", "create", request.Host + "/" + identifier, request.IsPrivate ? "--private" : "--public"]),
            SourceControlProvider.GitLab => request.ResumeExisting
                ? ("glab", ["api", "--hostname", request.Host!, "--method", "GET", "projects/" + Uri.EscapeDataString(identifier)])
                : ("glab", ["api", "--hostname", request.Host!, "--method", "POST", "projects", "--raw-field", "path=" + request.RepositoryName,
                    "--raw-field", "name=" + request.RepositoryName, "--raw-field", "visibility=" + (request.IsPrivate ? "private" : "public"),
                    "--raw-field", "namespace_id=" + (namespaceId ?? throw new PublicationFailure("Resolve the GitLab namespace before creation."))]),
            SourceControlProvider.AzureDevOps => ("az", ["repos", request.ResumeExisting ? "show" : "create", request.ResumeExisting ? "--repository" : "--name",
                request.RepositoryName, "--organization", request.OrganizationUrl!, "--project", request.Owner, "--detect", "false", "--output", "json", "--only-show-errors"]),
            _ => throw new PublicationFailure("This provider does not support publishing.")
        };
    }

    private static SourceControlRepository ReadPublicationRepository(PublishHostedRepositoryRequest request, string output)
    {
        using var document = JsonDocument.Parse(output);
        var item = document.RootElement;
        var rawUrl = Text(item, request.Provider switch { SourceControlProvider.GitHub or SourceControlProvider.Bitbucket => "clone_url", SourceControlProvider.GitLab => "http_url_to_repo", _ => "remoteUrl" });
        if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.UserInfo.Contains(':') || request.Provider != SourceControlProvider.AzureDevOps && uri.UserInfo.Length != 0)
            throw new PublicationFailure("The provider did not return a valid HTTPS repository URL.");
        if (request.Provider == SourceControlProvider.AzureDevOps && uri.Host.EndsWith(".visualstudio.com", StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort)
        {
            // Normalize Azure's two supported organization URL forms before comparing the
            // complete organization/project path. A different organization must still fail.
            uri = new UriBuilder(uri)
            {
                Host = "dev.azure.com", Path = "/" + uri.Host[..^".visualstudio.com".Length] + uri.AbsolutePath,
                UserName = "", Password = "",
            }.Uri;
        }
        var cleanUrl = new UriBuilder(uri) { UserName = "", Password = "" }.Uri.AbsoluteUri;
        var expectedHost = request.Provider == SourceControlProvider.AzureDevOps ? new Uri(request.OrganizationUrl!).Authority : request.Host;
        var expectedPath = request.Provider == SourceControlProvider.AzureDevOps
            ? new Uri(request.OrganizationUrl!).AbsolutePath.TrimEnd('/') + "/" + request.Owner + "/_git/" + request.RepositoryName
            : "/" + request.Owner + "/" + request.RepositoryName;
        var actualPath = Uri.UnescapeDataString(uri.AbsolutePath);
        if (actualPath.EndsWith(".git", StringComparison.Ordinal)) actualPath = actualPath[..^4];
        // A name ending in .git is a legal Azure repository name, not necessarily a suffix.
        if (!string.Equals(uri.Authority, expectedHost, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(actualPath, expectedPath, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(Uri.UnescapeDataString(uri.AbsolutePath), expectedPath, StringComparison.OrdinalIgnoreCase))
            throw new PublicationFailure("The provider returned a different repository. No remote was changed and nothing was pushed.");
        var webUrl = request.Provider != SourceControlProvider.AzureDevOps && cleanUrl.EndsWith(".git", StringComparison.Ordinal) ? cleanUrl[..^4] : cleanUrl;
        return new(request.Provider, uri.Authority, request.Owner, request.RepositoryName, webUrl, cleanUrl,
            Text(item, "default_branch") ?? "main", false);
    }

    private async Task<string> EnsurePublicationRemoteAsync(string url, string workspace, CancellationToken token)
    {
        var names = (await PublishGitAsync(["remote"], workspace, token).ConfigureAwait(false)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < 100; index++)
        {
            var name = index == 0 ? "origin" : "origin-" + index.ToString(CultureInfo.InvariantCulture);
            if (!names.Contains(name, StringComparer.Ordinal))
                await PublishGitAsync(["remote", "add", name, url], workspace, token).ConfigureAwait(false);
            var fetch = (await PublishGitAsync(["remote", "get-url", "--all", name], workspace, token).ConfigureAwait(false)).Trim();
            var push = (await PublishGitAsync(["remote", "get-url", "--push", "--all", name], workspace, token).ConfigureAwait(false)).Trim();
            if (fetch == url && push == url) return name;
            if (!names.Contains(name, StringComparer.Ordinal))
                throw new PublicationFailure("Git configuration rewrites the new remote to a different destination. Review it before resuming.");
        }
        throw new PublicationFailure("No unused publication remote name was found. Review the repository remotes before resuming.");
    }

    private async Task<string?> PublicationHeadAsync(string workspace, CancellationToken token)
    {
        var result = await RunHostingCommandAsync("git", ["rev-parse", "--verify", "--quiet", "HEAD"], workspace, token).ConfigureAwait(false);
        if (result.ExitCode == 1) return null;
        if (result.ExitCode != 0) throw new PublicationFailure("The local commit could not be inspected.");
        var hash = result.StandardOutput.Trim();
        if (hash.Length is not (40 or 64) || !hash.All(char.IsAsciiHexDigit)) throw new PublicationFailure("Git did not return a valid commit identity.");
        return hash;
    }

    private Task<string> PublishGitAsync(IReadOnlyList<string> args, string workspace, CancellationToken token) =>
        PublishCommandAsync("git", args, workspace, "Git publication step", token);

    private async Task<string> PublishCommandAsync(string tool, IReadOnlyList<string> args, string workspace, string step, CancellationToken token)
    {
        var result = await RunHostingCommandAsync(tool, args, workspace, token).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new PublicationFailure($"{step} failed. Check the {tool} tool, authentication, permissions, and destination on this host.");
        return result.StandardOutput;
    }

    private sealed class PublicationFailure(string message) : Exception(message);
}
