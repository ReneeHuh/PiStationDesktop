namespace PiStation.Host.Hosting;

internal static class HostDataLock
{
    public static FileStream Acquire(HostOptions options)
    {
        options.Validate();
        Directory.CreateDirectory(options.CanonicalDataRoot);
        try
        {
            return new FileStream(Path.Combine(options.CanonicalDataRoot, "environment.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new IOException("Another PiStation host already owns this data directory. Reuse it or choose a separate data directory.", exception);
        }
    }
}
