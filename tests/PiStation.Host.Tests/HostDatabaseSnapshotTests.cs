using Microsoft.Data.Sqlite;
using PiStation.Host.Updates;

namespace PiStation.Host.Tests;

public sealed class HostDatabaseSnapshotTests
{
    [Fact]
    public async Task OwnerRejectsPackagesThatCannotBlockWritesDuringStartupVerification()
    {
        using var folder = new HostTestDirectory();
        var package = folder.GetPath("old-package.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(package, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("pistation-update.json").Open()))
            {
                writer.Write(System.Text.Json.JsonSerializer.Serialize(new
                {
                    version = "1.0.0.0", protocolVersion = PiStation.Protocol.ProtocolVersion.Current,
                    platform = "win-x64", databaseCompatibilityVersion = 1,
                }));
            }
            archive.CreateEntry("PiStation.Server.exe");
        }
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            ServerUpdatePackage.ValidateAsync(package, folder.GetPath("runtime"), CancellationToken.None));
        Assert.Contains("startup safety", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotIncludesWalAndRestoresPreUpdateSchemaAndData()
    {
        using var folder = new HostTestDirectory();
        var snapshot = Path.Combine(folder.Path, "snapshot");
        using (var database = Open(folder.Path))
        {
            Execute(database, "PRAGMA journal_mode=WAL; CREATE TABLE Edits(Text TEXT); INSERT INTO Edits VALUES ('keep me');");
            HostDatabaseSnapshot.Create(folder.Path, snapshot);
            Execute(database, "DROP TABLE Edits; CREATE TABLE BrokenMigration(Value INTEGER);");
        }
        HostDatabaseSnapshot.Restore(folder.Path, snapshot);
        using var restored = Open(folder.Path);
        using var command = restored.CreateCommand();
        command.CommandText = "SELECT Text FROM Edits";
        Assert.Equal("keep me", command.ExecuteScalar());
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='BrokenMigration'";
        Assert.Equal(0L, command.ExecuteScalar());
    }

    [Fact]
    public void CorruptSnapshotDoesNotReplaceTheCurrentDatabase()
    {
        using var folder = new HostTestDirectory();
        var snapshot = Path.Combine(folder.Path, "snapshot");
        using (var database = Open(folder.Path))
        {
            Execute(database, "CREATE TABLE Edits(Text TEXT); INSERT INTO Edits VALUES ('current');");
            HostDatabaseSnapshot.Create(folder.Path, snapshot);
        }
        File.AppendAllText(Path.Combine(snapshot, "host.db"), "tampered");
        Assert.Throws<InvalidDataException>(() => HostDatabaseSnapshot.Restore(folder.Path, snapshot));
        using var current = Open(folder.Path);
        using var command = current.CreateCommand();
        command.CommandText = "SELECT Text FROM Edits";
        Assert.Equal("current", command.ExecuteScalar());
    }

    [Fact]
    public void CandidateHostRejectsWritesUntilTheOwnerConfirmsActivation()
    {
        using var folder = new HostTestDirectory();
        var owner = Path.Combine(folder.Path, "update-owner");
        Directory.CreateDirectory(owner);
        var handoff = Path.Combine(owner, "activate.json");
        File.WriteAllText(handoff, "pending");
        using var coordinator = new RemoteUpdateCoordinator(folder.Path, () => false);
        Assert.True(coordinator.IsDraining);
        Assert.Throws<InvalidOperationException>(() => coordinator.EnterOperation(allowDuringDrain: false));
        Assert.Null(coordinator.EnterOperation(allowDuringDrain: true));
        File.Delete(handoff);
        Assert.False(coordinator.IsDraining);
        using var lease = coordinator.EnterOperation(allowDuringDrain: false);
        Assert.NotNull(lease);
    }

    private static SqliteConnection Open(string root)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(root, "host.db"), Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection database, string sql)
    {
        using var command = database.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
