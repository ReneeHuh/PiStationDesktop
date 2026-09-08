using System.Text.Json;
using PiStation.Protocol.Models;

namespace PiStation.Host.Projects;

internal static class PiPackageCatalog
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 1024 * 1024 };

    internal static async Task<(IReadOnlyList<PiPackageSearchItem> Items, int? NextOffset)> SearchAsync(string query, int offset, CancellationToken token)
    {
        if (query.Length > 200 || query.Any(char.IsControl) || offset < 0 || offset > int.MaxValue - 20)
            throw new ArgumentException("Use up to 200 search characters and a valid package page.");
        var url = "https://registry.npmjs.org/-/v1/search?text=" + Uri.EscapeDataString("keywords:pi-package " + query.Trim()) +
            "&size=20&from=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var response = await Client.GetAsync(url, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        return Parse(document.RootElement, offset);
    }

    internal static (IReadOnlyList<PiPackageSearchItem> Items, int? NextOffset) Parse(JsonElement response, int offset)
    {
        var items = new List<PiPackageSearchItem>();
        foreach (var entry in response.GetProperty("objects").EnumerateArray().Take(20))
        {
            var package = entry.GetProperty("package");
            var name = package.GetProperty("name").GetString() ?? "";
            var version = package.GetProperty("version").GetString() ?? "";
            static bool ValidPart(string part) => part.Length > 0 && char.IsAsciiLetterOrDigit(part[0]) &&
                part.All(character => char.IsAsciiLetterOrDigit(character) || "._-".Contains(character));
            var parts = name.Split('/');
            var validName = parts.Length == 1 ? ValidPart(name) : parts.Length == 2 && parts[0].StartsWith('@') && ValidPart(parts[0][1..]) && ValidPart(parts[1]);
            if (name.Length is 0 or > 214 || !validName || version.Length is 0 or > 128 || !char.IsAsciiDigit(version[0]) ||
                version.Any(character => !char.IsAsciiLetterOrDigit(character) && ".+-".IndexOf(character) < 0)) continue;
            var description = package.TryGetProperty("description", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
            items.Add(new(name, version, description.Length > 1000 ? description[..1000] : description, "npm:" + name + "@" + version));
        }
        var count = response.GetProperty("objects").GetArrayLength();
        var total = response.GetProperty("total").GetInt64();
        return (items, count > 0 && (long)offset + count < total ? offset + count : null);
    }
}
