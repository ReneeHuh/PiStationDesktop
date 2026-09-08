using System.ComponentModel;

namespace PiStation.ClientRuntime.Ssh;

public enum SshSetupStep { Client, HostKey, Authentication, Host, Compatibility, Tunnel }
public enum SshSetupState { NotChecked, Checking, Passed, Failed, Canceled }

// Messages come from local, fixed guidance, never from remote output or credentials.
public sealed record SshSetupCheck(SshSetupStep Step, SshSetupState State, string Message)
{
    public string Title => Step switch
    {
        SshSetupStep.Client => "OpenSSH client and configuration",
        SshSetupStep.HostKey => "SSH reachability and host key",
        SshSetupStep.Authentication => "SSH account authentication",
        SshSetupStep.Host => "Running PiStation host",
        SshSetupStep.Compatibility => "Host identity and version",
        _ => "Encrypted tunnel and host response",
    };
    public string DisplayText => $"{(State == SshSetupState.NotChecked ? "Not checked" : State.ToString())} · {Title}" +
        (string.IsNullOrEmpty(Message) ? string.Empty : "\n" + Message);
}

internal sealed record SshSetupFailure(SshSetupStep Step, string Message, bool IsSpecific = true);

internal static class SshSetupDiagnostics
{
    internal static bool ForwardingDenied(string error) => Contains(error, "administratively prohibited") ||
        Contains(error, "port forwarding disabled") || Contains(error, "port forwarding is disabled");

    internal static SshSetupFailure FromStandardError(string error, bool forwardOnly)
    {
        if (Contains(error, "REMOTE HOST IDENTIFICATION HAS CHANGED"))
            return new(SshSetupStep.HostKey, "The saved SSH host key changed. Verify the new fingerprint with the host owner before correcting its known_hosts entry in a terminal, then run Check setup again.");
        if (Contains(error, "Host key verification failed"))
            return new(SshSetupStep.HostKey, "SSH could not verify this host key. Connect to the same target and port in a terminal, compare the fingerprint with the host owner, and accept it only after verification. Then run Check setup again.");
        if (Contains(error, "Bad configuration option") || Contains(error, "bad configuration options") || Contains(error, "Bad owner or permissions"))
            return new(SshSetupStep.Client, "OpenSSH rejected its configuration or file permissions. Check the target's entry in your SSH config and its file permissions using ssh in a terminal, then retry.");
        if (Contains(error, "Could not resolve hostname"))
            return new(SshSetupStep.HostKey, "The SSH hostname or config alias could not be resolved. Check the target spelling and SSH config; connect to the required LAN or VPN, then retry.");
        if (Contains(error, "Connection refused"))
            return new(forwardOnly ? SshSetupStep.Tunnel : SshSetupStep.HostKey, forwardOnly
                ? "The forwarded connection was refused. Check that PiStation is still running under the SSH account, then retry setup to discover its current port."
                : "The SSH server refused the connection. On the remote PC, check that OpenSSH Server is running and listening on the selected SSH port. Check its firewall rule, then retry.");
        if (Contains(error, "Connection timed out") || Contains(error, "No route to host") || Contains(error, "Network is unreachable"))
            return new(SshSetupStep.HostKey, "The SSH server is unreachable. Check the address and port, wake the remote PC, join its LAN or VPN, and check the host's SSH firewall rule.");
        if (Contains(error, "Permission denied") || Contains(error, "Authentication failed") || Contains(error, "Too many authentication failures"))
            return new(SshSetupStep.Authentication, "SSH authentication failed. Check the Windows username, key/agent or password, and the authentication methods enabled on the host. Test the same target in a terminal, then retry.");
        if (Contains(error, "PISTATION_HOST_WRONG_OWNER"))
            return new(SshSetupStep.Host, "The PiStation discovery endpoint belongs to another Windows account. Run PiStation under the account used for SSH, or connect using the account that owns the running host.");
        if (Contains(error, "PISTATION_HOST_AMBIGUOUS"))
            return new(SshSetupStep.Host, "Multiple PiStation data directories were found. Enter the data directory used by the running remote PiStation host, then check setup again.");
        if (Contains(error, "PISTATION_HOST_NOT_RUNNING"))
            return new(SshSetupStep.Host, "PiStation is not running for this SSH account and data directory. Start PiStation on the remote PC under the same Windows account. Check the host data directory and retry.");
        if (Contains(error, "PISTATION_HOST_DISCOVERY_FAILED"))
            return new(SshSetupStep.Host, "The running PiStation host could not be discovered. Wait for PiStation to finish starting under the SSH account and check that the host data directory matches.");
        if (ForwardingDenied(error))
            return new(SshSetupStep.Tunnel, "The SSH server denied port forwarding. Ask the host administrator to allow local TCP forwarding to PiStation's loopback port for this account, including any Match or PermitOpen restrictions. Then retry.");
        if (Contains(error, "Address already in use") || Contains(error, "cannot listen to port"))
            return new(SshSetupStep.Tunnel, "The local SSH forwarding port is unavailable. Run Check setup again to allocate a new local port, or close and reopen the saved connection.");
        if (Contains(error, "powershell.exe") && (Contains(error, "not recognized") || Contains(error, "not found")))
            return new(SshSetupStep.Host, "The remote SSH shell could not launch Windows PowerShell. Check that this is a Windows host and powershell.exe is available to its SSH account.");
        if (Contains(error, "PISTATION_INSTALL_FAILED"))
            return new(SshSetupStep.Host, "The bundled SSH host could not be installed. Check free space and write access to the remote account's LocalAppData. The running host and its data were not replaced.");
        if (Contains(error, "Pi installation") || Contains(error, "node"))
            return new(SshSetupStep.Host, "Pi or Node could not be found on the remote host. Check Pi's installation in the host's runtime settings and the non-interactive SSH account's PATH, then retry.");
        return new(forwardOnly ? SshSetupStep.Tunnel : SshSetupStep.HostKey,
            "SSH could not complete this check. Test the same target and port in a terminal, check the account and running PiStation host, then retry setup.", IsSpecific: false);
    }

    internal static SshSetupFailure FromException(Exception error, SshSetupStep step)
    {
        if (error is Win32Exception || error.InnerException is Win32Exception)
            return new(SshSetupStep.Client, "OpenSSH Client (ssh.exe) could not start. In Windows Settings, open Optional features and check that OpenSSH Client is installed. Confirm ssh -V works in a terminal, then retry.");
        if (error is ConnectionValidationException validation)
            return validation.Failure switch
            {
                ConnectionFailure.Protocol => new(SshSetupStep.Compatibility, "The host and desktop use incompatible versions. Use Install owner-managed update for a saved connection, or update the owning PiStation desktop/server locally, then retry."),
                ConnectionFailure.Certificate => new(SshSetupStep.Tunnel, "The host certificate did not match the identity received over SSH. Verify the host and data directory before reopening the connection."),
                ConnectionFailure.Authentication => new(step == SshSetupStep.Tunnel ? step : SshSetupStep.Authentication,
                    "Authentication was canceled or rejected. Retry with the correct account and credentials. If the tunnel rejected its host credential, reopen the connection."),
                _ => new(SshSetupStep.Compatibility, "The host environment or security identity changed. Verify the SSH target, account and host data directory before reopening the saved connection."),
            };
        if (error is TimeoutException)
            return new(step, step == SshSetupStep.Tunnel
                ? "The SSH tunnel did not return a host response in time. Check local TCP forwarding permissions and that PiStation is still running, then retry setup."
                : "The SSH check timed out. Check network reachability, finish any authentication prompt, and confirm PiStation is running under the SSH account, then retry.");
        if (error is OperationCanceledException) return new(step, "Setup check canceled. Run it again when ready.");
        return new(step, "This setup check could not complete. Check the target, SSH account and host data directory, then retry. If it persists, verify the connection using ssh in a terminal.");
    }

    private static bool Contains(string text, string value) => text.Contains(value, StringComparison.OrdinalIgnoreCase);
}
