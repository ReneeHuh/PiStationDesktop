namespace PiStation.Protocol.Models;

// Scopes are resources accessible to the host's active credentials, not account-switch commands.
public sealed record HostingBrowseLocation(SourceControlProvider Provider, string Host = "", string Organization = "");
public sealed record HostingAccountScope(string Id, string Name);
public sealed record ListHostingAccountsRequest(HostingBrowseLocation Location, int Page = 1);
public sealed record ListHostingAccountsResult(IReadOnlyList<HostingAccountScope> Accounts, int? NextPage, string Notice);
public sealed record BrowseHostedRepositoriesRequest(HostingBrowseLocation Location, string AccountId, int Page = 1);
public sealed record HostedRepositoryChoice(string Id, string Name, string WebUrl, string CloneUrl, bool IsPrivate);
public sealed record BrowseHostedRepositoriesResult(IReadOnlyList<HostedRepositoryChoice> Repositories, int? NextPage, string Notice);
