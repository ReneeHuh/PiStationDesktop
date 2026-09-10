namespace PiStation.ClientRuntime;

/// <summary>Owns one temporary environment and an exclusive crash-recovery lease.</summary>
public sealed class TemporarySessionStorage : IAsyncDisposable
{
    private FileStream? _lease;
    public string Root { get; }
    private TemporarySessionStorage(string root, FileStream lease) { Root = root; _lease = lease; }

    public static TemporarySessionStorage Create(string parent)
    {
        parent = Path.GetFullPath(parent);
        Directory.CreateDirectory(parent);
        RejectLinks(parent);
        var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new(root, new FileStream(Path.Combine(root, ".owner"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None));
    }

    public static IReadOnlyList<string> CleanAbandoned(string parent)
    {
        var failed = new List<string>();
        if (!Directory.Exists(parent)) return failed;
        parent = Path.GetFullPath(parent);
        RejectLinks(parent);
        foreach (var path in Directory.EnumerateDirectories(parent))
        {
            if (!Guid.TryParseExact(Path.GetFileName(path), "N", out _)) continue;
            try
            {
                RejectLinks(path);
                using (new FileStream(Path.Combine(path, ".owner"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }
                DeleteOwned(path);
            }
            catch (IOException) { failed.Add(path); }
            catch (UnauthorizedAccessException) { failed.Add(path); }
        }
        return failed;
    }

    public async ValueTask DisposeAsync()
    {
        if (_lease is not null) { await _lease.DisposeAsync().ConfigureAwait(false); _lease = null; }
        for (var attempt = 0; ; attempt++)
        {
            try { DeleteOwned(Root); return; }
            catch (IOException) when (attempt < 19) { await Task.Delay(250).ConfigureAwait(false); }
        }
    }

    private static void DeleteOwned(string root)
    {
        if (!Directory.Exists(root)) return;
        if (!Guid.TryParseExact(Path.GetFileName(root), "N", out _)) throw new IOException("Invalid temporary environment identity.");
        RejectLinks(root);
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                RejectLinks(path);
                if (Directory.Exists(path)) pending.Push(path);
            }
        Directory.Delete(root, recursive: true);
    }

    private static void RejectLinks(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Temporary storage cleanup refused a filesystem link: " + path);
    }
}
