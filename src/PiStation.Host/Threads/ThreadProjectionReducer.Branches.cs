using System.Text.Json;

namespace PiStation.Host.Threads;

public static partial class ThreadProjectionReducer
{
    private static IReadOnlyList<JsonElement> ActiveBranch(IReadOnlyList<JsonElement> entries, string? leafId)
    {
        // Keep compatibility with legacy linear fixtures; Pi v3 entries have explicit parentId.
        if (!entries.Any(entry => entry.TryGetProperty("parentId", out _))) return entries;
        var byId = entries.Where(entry => entry.TryGetProperty("parentId", out _) && entry.TryGetProperty("id", out _))
            .ToDictionary(entry => entry.GetProperty("id").GetString()!, StringComparer.Ordinal);
        var branch = new List<JsonElement>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (leafId is not null)
        {
            if (!visited.Add(leafId) || !byId.TryGetValue(leafId, out var entry))
                throw new InvalidDataException("The active Pi session branch contains a missing or cyclic entry.");
            branch.Add(entry);
            leafId = entry.GetProperty("parentId").GetString();
        }
        branch.Reverse();
        return branch;
    }
}
