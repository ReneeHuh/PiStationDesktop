namespace PiStation.ClientRuntime.Ssh;

public static class RemoteEditorLink
{
    public static Uri Create(SshConnectionProfile profile, string workspacePath, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        if (profile.Port is not null)
            throw new InvalidOperationException("For an external editor, save an SSH config alias with this host and port, then use that alias without a port override.");
        if (!Path.IsPathFullyQualified(workspacePath)) throw new ArgumentException("The host workspace must have an absolute path.", nameof(workspacePath));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath)) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The file must be inside the selected host workspace.", nameof(relativePath));
        var normalized = path.Replace('\\', '/');
        if (!normalized.StartsWith('/')) normalized = "/" + normalized;
        return new Uri("vscode://vscode-remote/ssh-remote+" + Uri.EscapeDataString(profile.Target) +
            string.Join('/', normalized.Split('/').Select(Uri.EscapeDataString)));
    }
}
