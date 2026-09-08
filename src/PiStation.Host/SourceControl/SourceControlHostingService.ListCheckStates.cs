using System.Globalization;
using System.Text.Json;
using PiStation.Protocol.Models;

namespace PiStation.Host.SourceControl;

public sealed partial class SourceControlHostingService
{
    private async Task<PullRequestDescriptor[]> ReadListCheckStatesAsync(SourceControlRepository repository, string workspace,
        PullRequestDescriptor[] rows, CancellationToken cancellationToken)
    {
        // gh's statusCheckRollup JSON field expands individual checks for every PR. On large lists
        // that query can time out at GitHub. Read only the rollup state for the displayed page.
        var fields = rows.Select((row, index) => $"pr{index.ToString(CultureInfo.InvariantCulture)}:pullRequest(number:{ValidateNumber(row.Number).ToString(CultureInfo.InvariantCulture)})" +
            "{number commits(last:1){nodes{commit{statusCheckRollup{state}}}}}");
        var query = "query PullRequestListCheckStates($owner:String!,$name:String!){repository(owner:$owner,name:$name){" + string.Join(' ', fields) + "}}";
        using var response = await ExecuteGraphQlAsync(repository, workspace, query, new { owner = repository.Owner, name = repository.Name }, cancellationToken).ConfigureAwait(false);
        var data = response.RootElement.GetProperty("data").GetProperty("repository");
        return rows.Select((row, index) => row with { Checks = ParseListCheckState(data.GetProperty("pr" + index.ToString(CultureInfo.InvariantCulture)), row.Number) }).ToArray();
    }

    internal static PullRequestCheckState ParseListCheckState(JsonElement pullRequest, string number)
    {
        if (pullRequest.ValueKind == JsonValueKind.Null) return PullRequestCheckState.Unknown;
        if (Text(pullRequest, "number") != number) throw ReviewError("The check status belongs to another pull request. Refresh the list.");
        var latest = Nodes(pullRequest.GetProperty("commits")).FirstOrDefault();
        return NestedText(latest, "commit", "statusCheckRollup", "state") switch
        {
            "SUCCESS" => PullRequestCheckState.Passed,
            "FAILURE" or "ERROR" => PullRequestCheckState.Failed,
            "PENDING" or "EXPECTED" => PullRequestCheckState.Pending,
            _ => PullRequestCheckState.Unknown
        };
    }
}
