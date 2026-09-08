using System.Text.Json;

namespace PiStation.Host.Updates;

public sealed class StandaloneUpdateOwner(string ownerDirectory, Action requestShutdown) : IRemoteUpdateOwner
{
    public string Kind => "standalone";
    public string CurrentVersion => typeof(EnvironmentService).Assembly.GetName().Version!.ToString();
    public string PackageKind => ".zip";
    public string Trust => "Packages supplied by approved operate devices; integrity checked, publisher identity is not asserted.";
    public Task<string> ValidateAsync(string packagePath, string runtimeDirectory, CancellationToken cancellationToken) =>
        ServerUpdatePackage.ValidateAsync(packagePath, runtimeDirectory, cancellationToken);
    public Task ActivateAsync(StagedRemoteUpdate update, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WriteHandoff(Path.Combine(ownerDirectory, "activate.json"), update);
        requestShutdown();
        return Task.CompletedTask;
    }

    public static void WriteHandoff(string path, StagedRemoteUpdate update)
    {
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(update, RemoteUpdateJsonContext.Default.StagedRemoteUpdate));
        File.Move(temporary, path, overwrite: false);
    }
    public static StagedRemoteUpdate ReadHandoff(string path) =>
        JsonSerializer.Deserialize(File.ReadAllBytes(path), RemoteUpdateJsonContext.Default.StagedRemoteUpdate) ?? throw new InvalidDataException("The activation request is invalid.");
}
