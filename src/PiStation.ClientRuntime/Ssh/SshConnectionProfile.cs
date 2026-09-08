using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using PiStation.Protocol.Identifiers;

namespace PiStation.ClientRuntime.Ssh;

public sealed record SshConnectionProfile(Guid Id, string Name, string Target, string ServerPath,
    string? DataRoot, string? PiExecutable, EnvironmentId? ExpectedEnvironmentId, ClientId ClientId, int? Port = null)
{
    public override string ToString() => Name;

    public void Validate()
    {
        if (Id == Guid.Empty) throw new ArgumentException("An SSH connection ID is required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        // ssh performs alias/config resolution. Reject options, shell metacharacters and URI
        // forms here; custom ports, jump hosts and identities belong in the OpenSSH config.
        if (string.IsNullOrWhiteSpace(Target) || Target.Length > 255 || Target[0] == '-' ||
            Target.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-' or '@' or ':' or '[' or ']')))
            throw new ArgumentException("Enter an SSH config alias or user@host. Configure custom ports and keys in your OpenSSH config.");
        ValidatePath(ServerPath, required: false);
        ValidatePath(DataRoot, required: false);
        ValidatePath(PiExecutable, required: false);
        if (Port is < 1 or > 65535) throw new ArgumentException("The SSH port must be between 1 and 65535.");
        ArgumentException.ThrowIfNullOrWhiteSpace(ClientId.Value);
    }

    private static void ValidatePath(string? value, bool required)
    {
        if (!required && string.IsNullOrWhiteSpace(value)) return;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2000 || value.Any(char.IsControl) || value.Contains('"', StringComparison.Ordinal))
            throw new ArgumentException("Enter a valid remote Windows path without quotes or control characters.");
    }
}

[SupportedOSPlatform("windows")]
public sealed class SshConnectionStore(string path)
{
    private readonly object _gate = new();

    public IReadOnlyList<SshConnectionProfile> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(path)) return [];
            var bytes = WindowsProtectedStorage.Read(path);
            try
            {
                var records = JsonSerializer.Deserialize(bytes, SshStorageJsonContext.Default.SshStorageEntryArray)
                    ?? throw new InvalidOperationException("Saved SSH connections could not be read.");
                var profiles = records.Select(p => new SshConnectionProfile(p.Id, p.Name, p.Target, p.ServerPath,
                    p.DataRoot ?? (p.SharedDefaultDataRoot ? null : @"%LOCALAPPDATA%\PiStation\ssh-host"),
                    p.PiExecutable, p.EnvironmentId is null ? null : EnvironmentId.Parse(p.EnvironmentId), ClientId.Parse(p.ClientId), p.Port)).ToArray();
                foreach (var profile in profiles) profile.Validate();
                return profiles;
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    public void Save(SshConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        lock (_gate) Write(Load().Where(p => p.Id != profile.Id).Append(profile).ToArray());
    }

    public void Forget(Guid id)
    {
        lock (_gate) Write(Load().Where(p => p.Id != id).ToArray());
    }

    private void Write(SshConnectionProfile[] profiles) => WindowsProtectedStorage.Write(path,
        JsonSerializer.SerializeToUtf8Bytes(profiles.Select(p => new SshStorageEntry(p.Id, p.Name, p.Target, p.ServerPath,
            p.DataRoot, p.PiExecutable, p.ExpectedEnvironmentId?.Value, p.ClientId.Value, true, p.Port)).ToArray(), SshStorageJsonContext.Default.SshStorageEntryArray));
}

internal sealed record SshStorageEntry(Guid Id, string Name, string Target, string ServerPath,
    string? DataRoot, string? PiExecutable, string? EnvironmentId, string ClientId, bool SharedDefaultDataRoot = false, int? Port = null);
[JsonSerializable(typeof(SshStorageEntry[]))]
internal sealed partial class SshStorageJsonContext : JsonSerializerContext;
