using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace PiStation.ClientRuntime.Ssh;

internal static class SshCommands
{
    public static ProcessStartInfo Control(SshConnectionProfile profile, string? authSecret = null)
    {
        profile.Validate();
        var start = Base(authSecret);
        AddPort(start, profile);
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add("ClearAllForwardings=yes");
        start.ArgumentList.Add(profile.Target);
        start.ArgumentList.Add("powershell.exe");
        start.ArgumentList.Add("-NoLogo");
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        // The outer cmd.exe/PowerShell shell sees only fixed switches and Base64. User paths
        // are literal single-quoted PowerShell values, never executable script fragments.
        var script = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[System.Text.UTF8Encoding]::new($false); ";
        if (string.IsNullOrWhiteSpace(profile.ServerPath))
        {
            // Keep the remote command below cmd.exe's command-line limit. The larger installer
            // is sent as data on stdin, followed (only on request) by the archive.
            script += "$s=[Console]::In.ReadLine(); if (!$s) { exit 1 }; & ([scriptblock]::Create([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($s)))); exit $LASTEXITCODE";
        }
        else
        {
            script += "& " + Quote(profile.ServerPath) + " 'attach'";
            if (!string.IsNullOrWhiteSpace(profile.DataRoot)) script += " '--data-root' " + DataRootArgument(profile.DataRoot);
            if (!string.IsNullOrWhiteSpace(profile.PiExecutable)) script += " '--pi-executable' " + Quote(profile.PiExecutable);
            script += "; exit $LASTEXITCODE";
        }
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        return start;
    }

    public static ProcessStartInfo Forward(SshConnectionProfile profile, int localPort, int remotePort, string? authSecret = null)
    {
        profile.Validate();
        ArgumentOutOfRangeException.ThrowIfLessThan(localPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(localPort, 65535);
        ArgumentOutOfRangeException.ThrowIfLessThan(remotePort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(remotePort, 65535);
        var start = Base(authSecret);
        AddPort(start, profile);
        foreach (var argument in new[] { "-N", "-n", "-o", "ExitOnForwardFailure=yes", "-L",
            string.Create(CultureInfo.InvariantCulture, $"127.0.0.1:{localPort}:127.0.0.1:{remotePort}"), profile.Target })
            start.ArgumentList.Add(argument);
        return start;
    }

    internal static ProcessStartInfo Base(string? authSecret = null)
    {
        var start = new ProcessStartInfo("ssh.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-T", "-o", authSecret is null ? "BatchMode=yes" : "BatchMode=no", "-o", "StrictHostKeyChecking=yes",
            "-o", "NumberOfPasswordPrompts=1",
            "-o", "ConnectTimeout=10", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=3",
            "-o", "ControlMaster=no", "-o", "ControlPath=none", "-o", "ControlPersist=no",
            "-o", "ForwardAgent=no", "-o", "ForwardX11=no", "-o", "PermitLocalCommand=no" })
            start.ArgumentList.Add(argument);
        // Ignore an inherited askpass configuration. Only the explicit in-app password retry
        // may provide one, and it must never turn host-key verification into an approval prompt.
        start.Environment.Remove("PISTATION_SSH_AUTH_SECRET");
        start.Environment.Remove("SSH_ASKPASS");
        start.Environment["SSH_ASKPASS_REQUIRE"] = "never";
        if (authSecret is not null)
        {
            var helper = Path.Combine(AppContext.BaseDirectory, "SshHost", "ssh-askpass.cmd");
            if (!File.Exists(helper)) throw new InvalidOperationException("The bundled SSH password helper is missing. Repair the desktop installation.");
            start.Environment["SSH_ASKPASS"] = helper;
            start.Environment["SSH_ASKPASS_REQUIRE"] = "force";
            start.Environment["PISTATION_SSH_AUTH_SECRET"] = authSecret;
        }
        return start;
    }

    internal static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    internal static string DataRootArgument(string value) => value.Contains('%', StringComparison.Ordinal)
        ? "([Environment]::ExpandEnvironmentVariables(" + Quote(value) + "))" : Quote(value);

    private static void AddPort(ProcessStartInfo start, SshConnectionProfile profile)
    {
        if (profile.Port is not { } port) return;
        start.ArgumentList.Add("-p");
        start.ArgumentList.Add(port.ToString(CultureInfo.InvariantCulture));
    }
}
