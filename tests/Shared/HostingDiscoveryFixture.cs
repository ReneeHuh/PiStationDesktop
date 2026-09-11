using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using PiStation.Host.SourceControl;

namespace PiStation.TestFixtures;

internal sealed class HostingDiscoveryFixture : HttpMessageHandler
{
    public const string ProjectId = "11111111-1111-1111-1111-111111111111";
    public ConcurrentQueue<(string Tool, string[] Args)> Commands { get; } = new();
    public ConcurrentQueue<string> Requests { get; } = new();
    public bool FullPage { get; set; }
    public bool Failure { get; set; }
    public string? OverrideJson { get; set; }
    public string? CloneOverride { get; set; }
    public BitbucketCloudClient Client() => new(this, () => new AuthenticationHeaderValue("Bearer", "isolated-discovery-token"));
    public Task<(int ExitCode, string StandardOutput, string StandardError)> Execute(string tool, IReadOnlyList<string> args,
        string directory, string? input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Commands.Enqueue((tool, args.ToArray()));
        if (Failure) return Task.FromResult((1, "secret-provider-output", "secret-provider-error"));
        if (OverrideJson is { } json) return Task.FromResult((0, json, ""));
        var path = args[^1]; var host = args.Contains("--hostname") ? args[args.ToList().IndexOf("--hostname") + 1] : "dev.azure.com";
        object payload;
        var count = FullPage ? 100 : 1;
        if (tool == "az")
        {
            payload = args[0] == "devops" ? new { value = new[] { new { id = ProjectId, name = "Team project" } } } :
                Enumerable.Range(1, FullPage ? 101 : 1).Select(i => new { id = i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture), name = "repo" + i,
                    webUrl = "https://dev.azure.com/acme/Team%20project/_git/repo" + i,
                    remoteUrl = CloneOverride ?? "https://acme@dev.azure.com/acme/Team%20project/_git/repo" + i }).ToArray();
        }
        else if (path == "user") payload = new { login = "active-user", username = "active-user" };
        else if (path.StartsWith("user/orgs?", StringComparison.Ordinal)) payload = Enumerable.Range(1, count).Select(i => new { login = "team" + i }).ToArray();
        else if (path.StartsWith("groups?", StringComparison.Ordinal)) payload = Enumerable.Range(1, count).Select(i => new { id = i, full_path = "team/subgroup" + i }).ToArray();
        else payload = Enumerable.Range(1, count).Select(i => new { id = i, full_name = "team1/repo" + i, path_with_namespace = "team/subgroup/repo" + i,
            html_url = "https://" + host + "/team1/repo" + i, clone_url = CloneOverride ?? "https://" + host + "/team1/repo" + i + ".git",
            web_url = "https://" + host + "/team/subgroup/repo" + i, http_url_to_repo = CloneOverride ?? "https://" + host + "/team/subgroup/repo" + i + ".git",
            visibility = "private", @private = true }).ToArray();
        return Task.FromResult((0, JsonSerializer.Serialize(payload), ""));
    }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Requests.Enqueue(request.RequestUri!.PathAndQuery);
        var path = request.RequestUri.AbsolutePath;
        object payload = path.EndsWith("user/workspaces", StringComparison.Ordinal)
            ? new { values = new[] { new { workspace = new { slug = "team", name = "Team workspace" } } }, next = FullPage ? "https://untrusted.example/not-followed" : "" }
            : new { values = new[] { new { uuid = "{repo-id}", full_name = "team/repo", is_private = true,
                links = new { html = new { href = CloneOverride ?? "https://bitbucket.org/team/repo" } } } }, next = FullPage ? "https://untrusted.example/not-followed" : "" };
        return Task.FromResult(new HttpResponseMessage(Failure ? HttpStatusCode.Forbidden : HttpStatusCode.OK)
        { Content = new StringContent(OverrideJson ?? JsonSerializer.Serialize(payload)) });
    }
}
