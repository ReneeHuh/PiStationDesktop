using System.Globalization;
using System.Text.Json;
using PiStation.Protocol.Models;

namespace PiStation.Host.SourceControl;

public sealed partial class SourceControlHostingService
{
    private const int DiscoveryPageSize = 100;
    private const int DiscoveryMaximumPages = 20;
    private static string DiscoverySegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value is "." or ".." || value.StartsWith('-') ||
            value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '{' or '}')))
            throw ReviewError("Choose a valid hosting account or project.");
        return Uri.EscapeDataString(value);
    }
    private static HostingBrowseLocation NormalizeBrowseLocation(HostingBrowseLocation location, int page)
    {
        if (page is < 1 or > DiscoveryMaximumPages) throw ReviewError("Hosting browsing is limited to twenty pages. Narrow the account scope.");
        var host = location.Host.Trim().ToLowerInvariant();
        if (host.Length == 0) host = location.Provider switch
        {
            SourceControlProvider.GitHub => "github.com", SourceControlProvider.GitLab => "gitlab.com",
            SourceControlProvider.Bitbucket => "bitbucket.org", SourceControlProvider.AzureDevOps => "dev.azure.com",
            _ => throw ReviewError("Choose a supported hosting provider.")
        };
        if (host.Length > 253 || Uri.CheckHostName(host) != UriHostNameType.Dns || host.Contains(':') ||
            host.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '.')) ||
            host.Split('.').Any(label => label.Length is 0 or > 63 || !char.IsAsciiLetterOrDigit(label[0]) || !char.IsAsciiLetterOrDigit(label[^1])))
            throw ReviewError("Enter a hosting server name without a scheme, path or credentials.");
        if (location.Provider == SourceControlProvider.Bitbucket && host != "bitbucket.org" ||
            location.Provider == SourceControlProvider.AzureDevOps && host != "dev.azure.com")
            throw ReviewError("Use bitbucket.org for Bitbucket Cloud or dev.azure.com for Azure DevOps.");
        if (location.Provider is not (SourceControlProvider.GitHub or SourceControlProvider.GitLab or SourceControlProvider.Bitbucket or SourceControlProvider.AzureDevOps))
            throw ReviewError("Choose a supported hosting provider.");
        var organization = location.Organization.Trim();
        if (location.Provider == SourceControlProvider.AzureDevOps) _ = DiscoverySegment(organization);
        return location with { Host = host, Organization = organization };
    }
    private Task<JsonElement> DiscoveryJsonAsync(HostingBrowseLocation location, string path, CancellationToken token) =>
        location.Provider == SourceControlProvider.Bitbucket ? _bitbucket.JsonAsync(path, token: token) :
        ProviderJsonAsync(location.Provider == SourceControlProvider.GitHub ? "gh" : "glab",
            ["api", "--hostname", location.Host, "--method", "GET", path], Environment.CurrentDirectory, null, token);

    private static JsonElement[] DiscoveryArray(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Array || json.GetArrayLength() > 2000)
            throw ReviewError("The hosting provider returned an invalid or oversized repository list.");
        return json.EnumerateArray().ToArray();
    }
    private static int? DiscoveryNext(int page, bool more) => more && page < DiscoveryMaximumPages ? page + 1 : null;
    private static string DiscoveryNotice(int page, bool more) => page == DiscoveryMaximumPages && more
        ? "The twenty-page limit was reached. Choose a narrower account scope."
        : "Uses the host's active credentials. Account scopes do not switch sign-in. Filtering matches loaded repositories only. Git clone uses existing Git credentials.";

    public async Task<ListHostingAccountsResult> ListHostingAccountsAsync(ListHostingAccountsRequest request, CancellationToken token = default)
    {
        var location = NormalizeBrowseLocation(request.Location, request.Page);
        var page = request.Page.ToString(CultureInfo.InvariantCulture);
        var scopes = new List<HostingAccountScope>();
        JsonElement[] values;
        bool more;
        switch (location.Provider)
        {
            case SourceControlProvider.GitHub:
            case SourceControlProvider.GitLab:
                var github = location.Provider == SourceControlProvider.GitHub;
                if (request.Page == 1)
                {
                    var user = await DiscoveryJsonAsync(location, "user", token).ConfigureAwait(false);
                    var name = ProviderText(user, github ? "login" : "username");
                    if (name.Length == 0) throw ReviewError("The provider did not identify the active account.");
                    scopes.Add(new("all", name + " — all accessible member repositories"));
                }
                values = DiscoveryArray(await DiscoveryJsonAsync(location,
                    (github ? "user/orgs?" : "groups?all_available=false&") + "per_page=100&page=" + page, token).ConfigureAwait(false));
                if (values.Length > DiscoveryPageSize) throw ReviewError("The provider exceeded the account page size.");
                scopes.AddRange(values.Select(value => new HostingAccountScope(
                    "group:" + (github ? ProviderText(value, "login") : ProviderText(value, "id")),
                    ProviderText(value, github ? "login" : "full_path"))));
                more = values.Length == DiscoveryPageSize;
                break;
            case SourceControlProvider.Bitbucket:
                var memberships = await DiscoveryJsonAsync(location, "user/workspaces?pagelen=100&page=" + page, token).ConfigureAwait(false);
                values = DiscoveryArray(ProviderObject(memberships, "values"));
                if (values.Length > DiscoveryPageSize) throw ReviewError("The provider exceeded the account page size.");
                scopes.AddRange(values.Select(value => ProviderObject(value, "workspace")).Select(workspace =>
                    new HostingAccountScope(ProviderText(workspace, "slug"), ProviderText(workspace, "name"))));
                more = ProviderText(memberships, "next").Length > 0;
                break;
            default:
                var projects = await ProviderJsonAsync("az", ["devops", "project", "list", "--organization", "https://dev.azure.com/" + location.Organization,
                    "--top", "100", "--skip", ((request.Page - 1) * DiscoveryPageSize).ToString(CultureInfo.InvariantCulture),
                    "--state-filter", "wellFormed", "--output", "json", "--detect", "false"], Environment.CurrentDirectory, null, token).ConfigureAwait(false);
                values = DiscoveryArray(ProviderObject(projects, "value"));
                if (values.Length > DiscoveryPageSize) throw ReviewError("The provider exceeded the project page size.");
                scopes.AddRange(values.Select(value => new HostingAccountScope(ProviderText(value, "id"), ProviderText(value, "name"))));
                more = values.Length == DiscoveryPageSize;
                break;
        }
        if (scopes.Any(scope => string.IsNullOrWhiteSpace(scope.Name) || string.IsNullOrWhiteSpace(scope.Id) || scope.Id.EndsWith(':')))
            throw ReviewError("The provider returned an incomplete account identity.");
        return new(scopes.DistinctBy(scope => scope.Id).ToArray(), DiscoveryNext(request.Page, more), DiscoveryNotice(request.Page, more));
    }

    public async Task<BrowseHostedRepositoriesResult> BrowseHostedRepositoriesAsync(BrowseHostedRepositoriesRequest request, CancellationToken token = default)
    {
        var location = NormalizeBrowseLocation(request.Location, request.Page);
        var page = request.Page.ToString(CultureInfo.InvariantCulture);
        var scope = request.AccountId;
        string path;
        JsonElement[] values;
        bool more;
        switch (location.Provider)
        {
            case SourceControlProvider.GitHub:
            case SourceControlProvider.GitLab:
                var github = location.Provider == SourceControlProvider.GitHub;
                if (scope == "all") path = github ? "user/repos?affiliation=owner,collaborator,organization_member&" : "projects?membership=true&";
                else if (scope.StartsWith("group:", StringComparison.Ordinal))
                    path = (github ? "orgs/" : "groups/") + DiscoverySegment(scope[6..]) + (github ? "/repos?" : "/projects?include_subgroups=true&with_shared=false&");
                else throw ReviewError("Choose an account returned by the hosting browser.");
                path += github ? "sort=full_name&direction=asc&" : "order_by=id&sort=asc&";
                values = DiscoveryArray(await DiscoveryJsonAsync(location, path + "per_page=100&page=" + page, token).ConfigureAwait(false));
                if (values.Length > DiscoveryPageSize) throw ReviewError("The provider exceeded the repository page size.");
                more = values.Length == DiscoveryPageSize;
                break;
            case SourceControlProvider.Bitbucket:
                var repos = await DiscoveryJsonAsync(location, "repositories/" + DiscoverySegment(scope) + "?pagelen=100&page=" + page + "&sort=name", token).ConfigureAwait(false);
                values = DiscoveryArray(ProviderObject(repos, "values"));
                if (values.Length > DiscoveryPageSize) throw ReviewError("The provider exceeded the repository page size.");
                more = ProviderText(repos, "next").Length > 0;
                break;
            default:
                _ = DiscoverySegment(scope);
                var azure = DiscoveryArray(await ProviderJsonAsync("az", ["repos", "list", "--organization", "https://dev.azure.com/" + location.Organization,
                    "--project", scope, "--detect", "false", "--output", "json"], Environment.CurrentDirectory, null, token).ConfigureAwait(false));
                values = azure.OrderBy(value => ProviderText(value, "id"), StringComparer.Ordinal).Skip((request.Page - 1) * DiscoveryPageSize).Take(DiscoveryPageSize).ToArray();
                more = azure.Length > request.Page * DiscoveryPageSize;
                break;
        }
        var choices = values.Select(value => ParseDiscoveredRepository(location, value)).DistinctBy(repo => repo.Id).ToArray();
        if (location.Provider == SourceControlProvider.Bitbucket && choices.Any(repo => !repo.Name.StartsWith(scope + '/', StringComparison.OrdinalIgnoreCase)))
            throw ReviewError("The provider returned a repository from a different workspace.");
        return new(choices, DiscoveryNext(request.Page, more), DiscoveryNotice(request.Page, more));
    }

    private static HostedRepositoryChoice ParseDiscoveredRepository(HostingBrowseLocation location, JsonElement value)
    {
        var provider = location.Provider;
        var name = ProviderText(value, provider switch { SourceControlProvider.GitHub or SourceControlProvider.Bitbucket => "full_name", SourceControlProvider.GitLab => "path_with_namespace", _ => "name" });
        var id = ProviderText(value, provider == SourceControlProvider.Bitbucket ? "uuid" : "id");
        var web = provider == SourceControlProvider.Bitbucket ? BbLink(value, "html") : ProviderText(value, provider switch { SourceControlProvider.GitHub => "html_url", SourceControlProvider.GitLab => "web_url", _ => "webUrl" });
        var clone = ProviderText(value, provider switch { SourceControlProvider.GitHub => "clone_url", SourceControlProvider.GitLab => "http_url_to_repo", _ => "remoteUrl" });
        if (provider == SourceControlProvider.Bitbucket) clone = web.TrimEnd('/') + ".git";
        // Azure's HTTPS clone URL may contain the organization as userinfo. Strip it; never return credentials.
        if (provider == SourceControlProvider.AzureDevOps && Uri.TryCreate(clone, UriKind.Absolute, out var azureClone) && azureClone.Scheme == "https")
            clone = new UriBuilder(azureClone) { UserName = "", Password = "" }.Uri.AbsoluteUri;
        static bool SafeUrl(string url, string host) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
            uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
        if (name.Length is 0 or > 1024 || id.Length is 0 or > 256 || name.Any(char.IsControl) || id.Any(char.IsControl) ||
            !SafeUrl(web, location.Host) || !SafeUrl(clone, location.Host))
            throw ReviewError("The provider returned an invalid repository identity or clone URL.");
        if (provider == SourceControlProvider.AzureDevOps &&
            (!new Uri(web).AbsolutePath.StartsWith('/' + location.Organization + '/', StringComparison.OrdinalIgnoreCase) ||
             !new Uri(clone).AbsolutePath.StartsWith('/' + location.Organization + '/', StringComparison.OrdinalIgnoreCase)))
            throw ReviewError("The repository belongs to a different Azure organization.");
        if (provider != SourceControlProvider.AzureDevOps &&
            (Uri.UnescapeDataString(new Uri(web).AbsolutePath.Trim('/')) != name ||
             !string.Equals(clone.TrimEnd('/'), web.TrimEnd('/') + ".git", StringComparison.OrdinalIgnoreCase)))
            throw ReviewError("The repository name and clone URL do not identify the same repository.");
        return new(id, name, web, clone, provider == SourceControlProvider.GitLab ? ProviderText(value, "visibility") != "public" :
            provider == SourceControlProvider.Bitbucket ? ProviderBool(value, "is_private") : provider == SourceControlProvider.AzureDevOps || ProviderBool(value, "private"));
    }
}
