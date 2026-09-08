using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace PiStation.Host.Updates;

/// <summary>Update-owner snapshots. Restore only after stopping the owned host.</summary>
public static class HostDatabaseSnapshot
{
    public static bool Exists(string directory) => File.Exists(Path.Combine(directory, "host.db.sha256"));

    public static void Create(string dataRoot, string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "host.db");
        var temporary = path + ".tmp";
        using (var source = Open(Path.Combine(dataRoot, "host.db"), SqliteOpenMode.ReadOnly))
        using (var target = Open(temporary, SqliteOpenMode.ReadWriteCreate))
            source.BackupDatabase(target);
        Validate(temporary);
        File.Move(temporary, path, overwrite: true);
        File.WriteAllText(path + ".sha256.tmp", Hash(path));
        File.Move(path + ".sha256.tmp", path + ".sha256", overwrite: true);
    }

    public static void Restore(string dataRoot, string directory)
    {
        var source = Path.Combine(directory, "host.db");
        if (!string.Equals(File.ReadAllText(source + ".sha256"), Hash(source), StringComparison.Ordinal))
            throw new InvalidDataException("The pre-update database snapshot failed its integrity check.");
        Validate(source);
        var target = Path.Combine(dataRoot, "host.db");
        // Prepare the replacement before touching the database. No host may be running here.
        File.Copy(source, target + ".restore", overwrite: true);
        File.Delete(target + "-wal");
        File.Delete(target + "-shm");
        File.Move(target + ".restore", target, overwrite: true);
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = mode, Pooling = false }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static void Validate(string path)
    {
        using var database = Open(path, SqliteOpenMode.ReadOnly);
        using var command = database.CreateCommand();
        command.CommandText = "PRAGMA quick_check";
        if (!Equals(command.ExecuteScalar(), "ok")) throw new InvalidDataException("The database snapshot is not healthy.");
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
