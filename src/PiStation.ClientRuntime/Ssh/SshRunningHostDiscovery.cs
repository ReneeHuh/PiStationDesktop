using System.Text;

namespace PiStation.ClientRuntime.Ssh;

internal static class SshRunningHostDiscovery
{
    public static string EncodedScript(SshConnectionProfile profile)
    {
        profile.Validate();
        var root = string.IsNullOrWhiteSpace(profile.DataRoot) ? "$null" : SshCommands.DataRootArgument(profile.DataRoot);
        var script = "$dataRoot=" + root + ";\n" + File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "SshHost", "discover-host.ps1"));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
    }
}
