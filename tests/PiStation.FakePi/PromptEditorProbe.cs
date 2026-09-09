using System.Text;

namespace PiStation.FakePi;

internal static class PromptEditorProbe
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        var path = arguments[^1];
        if (arguments.Contains("--fail", StringComparer.Ordinal)) return 23;
        if (arguments.Contains("--hold", StringComparer.Ordinal))
        {
            await File.WriteAllTextAsync(path + ".opened", "ready");
            while (!File.Exists(path + ".finish")) await Task.Delay(50);
        }
        if (!arguments.Contains("--unchanged", StringComparer.Ordinal))
            await File.WriteAllTextAsync(path, "Edited outside PiStation.\nSecond line 😀", new UTF8Encoding(false));
        return 0;
    }
}
