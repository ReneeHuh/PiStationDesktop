using System.Text.Json;
using PiStation.PiRpc.Decoding;

namespace PiStation.PiRpc.Tests;

public sealed class PiUsageCostTests
{
    [Theory]
    [InlineData("input", "\"bad\"")]
    [InlineData("output", "null")]
    [InlineData("cacheRead", "-1")]
    [InlineData("reasoning", "\"bad\"")]
    public void InvalidUsageFieldsAreUnknownInsteadOfThrowing(string field, string value)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse("""{"usage":{"input":10,"output":2,"cacheRead":0,"cacheWrite":0,"totalTokens":12,"reasoning":1}}""")!;
        json["usage"]![field] = System.Text.Json.Nodes.JsonNode.Parse(value);
        using var document = JsonDocument.Parse(json.ToJsonString());
        Assert.Null(PiUsageReader.Read(document.RootElement));
    }

    [Theory]
    [InlineData("{\"total\":0.25}", "0.25")]
    [InlineData("{\"total\":0}", "0")]
    [InlineData("{\"total\":-1}", null)]
    [InlineData("{}", null)]
    [InlineData("null", null)]
    public void ReadsReportedCostWithoutInventingMissingValues(string cost, string? expected)
    {
        using var document = JsonDocument.Parse("{\"usage\":{\"input\":10,\"output\":2,\"cacheRead\":0,\"cacheWrite\":0,\"totalTokens\":12,\"cost\":" + cost + "}}");
        var usage = PiUsageReader.Read(document.RootElement);
        Assert.NotNull(usage);
        Assert.Equal(expected is null ? (decimal?)null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), usage.TotalCost);
    }
}
