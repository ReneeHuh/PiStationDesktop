using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using PiStation.Host.Hosting;
using PiStation.Host.Security;
using PiStation.Host.SourceControl;
using PiStation.Protocol.Identifiers;
using PiStation.Protocol.Models;
using PiStation.Protocol.Receipts;

namespace PiStation.ClientRuntime.Tests;

public sealed class RepositoryPublicationTransportTests
{
    [Theory]
    [InlineData(SourceControlProvider.GitLab, false)]
    [InlineData(SourceControlProvider.GitLab, true)]
    [InlineData(SourceControlProvider.AzureDevOps, false)]
    [InlineData(SourceControlProvider.AzureDevOps, true)]
    public async Task PublicationProgressReplaysAcrossReconnectAndTlsRejectsReadOnlyWrites(SourceControlProvider provider, bool tls)
    {
        using var directory = new ClientTestDirectory();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var commands = new PublicationCommands(provider);
        await using var local = await EmbeddedEnvironmentHost.StartAsync(directory.CreateHostOptions(),
            sourceControlFactory: (resolver, projects) => new SourceControlHostingService(resolver, projects, commands.RunAsync));
        using var access = new RemoteAccessStore(Path.Combine(directory.CreateDirectory("access"), "access.db"));
        using var key = RSA.Create(2048);
        using var created = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var certificate = X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.UserKeySet);
        await using var remote = tls ? await RemoteEnvironmentHost.StartAsync(local.Environment, access, IPAddress.Loopback, 0, certificate, cancellationToken: timeout.Token) : null;
        var options = new ClientRuntimeOptions
        {
            HubAddress = remote is null ? local.HubAddress : new(remote.Address, "/environment"),
            BearerCredential = remote is null ? local.BearerCredential : access.IssueSession(RemoteAccessLevel.Operate).Token,
            CertificateFingerprint = remote is null ? null : certificate.GetCertHashString(HashAlgorithmName.SHA256),
        };
        await using var client = new EnvironmentClient(options);
        await client.ConnectAsync(timeout.Token);
        var project = await client.AddProjectAsync(new(directory.CreateDirectory("empty-project")), timeout.Token);
        var request = new PublishHostedRepositoryRequest(project.ProjectId, provider,
            provider == SourceControlProvider.GitLab ? "team/subgroup" : "Team Project", "repo", IsPrivate: false, OperationId: CommandId.New(),
            Host: "gitlab.example", OrganizationUrl: "https://dev.azure.com/station");
        if (remote is not null)
        {
            await using var viewer = new EnvironmentClient(new()
            {
                HubAddress = options.HubAddress, CertificateFingerprint = options.CertificateFingerprint,
                BearerCredential = access.IssueSession(RemoteAccessLevel.ReadOnly).Token,
            });
            await viewer.ConnectAsync(timeout.Token);
            await Assert.ThrowsAnyAsync<Exception>(() => viewer.PublishHostedRepositoryAsync(request, timeout.Token));
            Assert.Equal(0, commands.Creates);
        }
        var first = await client.PublishHostedRepositoryAsync(request, timeout.Token);
        Assert.True(first.Succeeded, first.Message);
        Assert.Equal(RepositoryPublicationStage.RemoteConfigured, first.Publication!.Stage);
        Assert.Equal(commands.RemoteUrl, first.Publication.RemoteUrl);
        Assert.Equal("origin", first.Publication.RemoteName);
        Assert.Equal("main", first.Publication.Branch);
        await client.DisconnectAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        var replay = await client.PublishHostedRepositoryAsync(request, timeout.Token);
        Assert.Equal(first, replay);
        Assert.Equal(1, commands.Creates);
        var resume = await client.PublishHostedRepositoryAsync(request with { ResumeExisting = true, OperationId = CommandId.New() }, timeout.Token);
        Assert.True(resume.Succeeded, resume.Message);
        Assert.Equal(CommandReceiptState.Completed, resume.State);
        Assert.Equal(1, commands.Creates);
    }

    private sealed class PublicationCommands(SourceControlProvider provider)
    {
        private bool _hasRemote;
        public int Creates { get; private set; }
        public string RemoteUrl => provider == SourceControlProvider.GitLab
            ? "https://gitlab.example/team/subgroup/repo.git" : "https://dev.azure.com/station/Team%20Project/_git/repo";
        public Task<(int, string, string)> RunAsync(string tool, IReadOnlyList<string> args, string root, string? input, CancellationToken token)
        {
            Assert.Null(input);
            if (tool == "git")
            {
                if (args[0] == "symbolic-ref") return Result("main");
                if (args[0] == "rev-parse") return Task.FromResult((1, "", ""));
                Assert.Equal("remote", args[0]);
                if (args.Count == 1) return Result(_hasRemote ? "origin" : "");
                if (args[1] == "add") { Assert.Equal(RemoteUrl, args[^1]); _hasRemote = true; return Result(""); }
                Assert.Equal("get-url", args[1]);
                return Result(RemoteUrl);
            }
            if (tool == "glab")
            {
                Assert.Contains("gitlab.example", args);
                if (args.Contains("namespaces/team%2Fsubgroup")) return Result("""{"id":17,"full_path":"team/subgroup"}""");
                if (args.Contains("POST")) { Assert.Contains("visibility=public", args); Creates++; }
                else Assert.Contains("projects/team%2Fsubgroup%2Frepo", args);
            }
            else
            {
                Assert.Equal("az", tool);
                Assert.Contains("https://dev.azure.com/station", args);
                Assert.Contains("Team Project", args);
                if (args.Contains("create")) Creates++;
                else Assert.Contains("show", args);
            }
            return Result(JsonSerializer.Serialize(new Dictionary<string, string>
            { [provider == SourceControlProvider.GitLab ? "http_url_to_repo" : "remoteUrl"] = RemoteUrl }));
        }
        private static Task<(int, string, string)> Result(string output) => Task.FromResult((0, output, ""));
    }
}
