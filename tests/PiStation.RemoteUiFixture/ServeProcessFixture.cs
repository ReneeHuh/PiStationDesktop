using System.Diagnostics;
using System.Globalization;
using PiStation.ClientRuntime;

namespace PiStation.RemoteUiFixture;

// A real, bounded Windows process tree for testing the production Serve process owner.
// It never runs Tailscale, opens a listener, or touches application data.
internal static class ServeProcessFixture
{
    public static async Task<int> RunAsync(string mode, string root)
    {
        if (!Directory.Exists(root)) return 2;
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        if (mode == "owner")
        {
            await using var owned = new TailscaleServeProcess(Environment.ProcessPath!, Arguments("tree", root));
            while (!File.Exists(Path.Combine(root, "leaf.pid"))) await Task.Delay(20, lifetime.Token);
            await WritePidAsync(root, mode, lifetime.Token);
            await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token);
        }
        else if (mode == "tree")
        {
            using var leaf = Process.Start(StartInfo("leaf", root))!;
            try
            {
                await WritePidAsync(root, mode, lifetime.Token);
                await leaf.WaitForExitAsync(lifetime.Token);
            }
            finally { if (!leaf.HasExited) leaf.Kill(entireProcessTree: true); }
        }
        else
        {
            await WritePidAsync(root, mode, lifetime.Token);
            switch (mode)
            {
                case "query": Console.Write("{}"); return 0;
                case "error": Console.Error.Write("fixture-private-detail"); return 7;
                case "stdout-limit": Console.Write(new string('x', 4 * 1024 * 1024 + 1)); break;
                case "stderr-limit": Console.Error.Write(new string('x', 32 * 1024 + 1)); break;
                case "wait" or "leaf": break;
                default: return 2;
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, lifetime.Token);
        }
        return 0;
    }

    private static string[] Arguments(string mode, string root) => ["--serve-process-fixture", mode, root];

    private static ProcessStartInfo StartInfo(string mode, string root)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in Arguments(mode, root)) start.ArgumentList.Add(argument);
        return start;
    }

    private static async Task WritePidAsync(string root, string mode, CancellationToken token)
    {
        var path = Path.Combine(root, mode + ".pid");
        await File.WriteAllTextAsync(path + ".tmp", Environment.ProcessId.ToString(CultureInfo.InvariantCulture), token);
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
