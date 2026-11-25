using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Yastık6.Api.Infrastructure.Persistence;

namespace Yastık6.Api.Tests.Infrastructure;

public class SqliteMaintenanceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"yastik-maint-{Guid.NewGuid():N}.db");

    private ApplicationDbContext NewContext() => new(
        new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite($"Data Source={_dbPath}").Options);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            if (File.Exists(_dbPath + suffix)) File.Delete(_dbPath + suffix);
        }
    }

    [Fact]
    public void ClearStaleMigrationLock_RemovesLeftoverRow_SoMigrateCanProceed()
    {
        using var context = NewContext();
        context.Database.Migrate();

        // Simulate a previous start that was killed while holding the lock.
        context.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS __EFMigrationsLock (Id INTEGER PRIMARY KEY, Timestamp TEXT NOT NULL);");
        context.Database.ExecuteSqlRaw("INSERT OR REPLACE INTO __EFMigrationsLock (Id, Timestamp) VALUES (1, '2026-11-25 17:56:10');");

        var cleared = SqliteMaintenance.ClearStaleMigrationLock(context);

        cleared.Should().Be(1);
        context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM __EFMigrationsLock").Single().Should().Be(0);
    }

    [Fact]
    public void ClearStaleMigrationLock_OnFreshDatabase_DoesNotThrow()
    {
        using var context = NewContext();

        var act = () => SqliteMaintenance.ClearStaleMigrationLock(context);

        act.Should().NotThrow();
        act().Should().Be(0);
    }

    [Fact]
    public void CheckpointWal_FoldsWalIntoMainFileAndTruncatesIt()
    {
        using var context = NewContext();
        context.Database.Migrate();
        context.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        context.Database.ExecuteSqlRaw("CREATE TABLE Filler (Id INTEGER PRIMARY KEY, Data TEXT);");
        for (var i = 0; i < 200; i++)
        {
            context.Database.ExecuteSqlRaw("INSERT INTO Filler (Data) VALUES ({0});", new string('x', 2000));
        }
        new FileInfo(_dbPath + "-wal").Length.Should().BeGreaterThan(0);

        var result = SqliteMaintenance.CheckpointWal(context);

        result.Busy.Should().BeFalse();
        new FileInfo(_dbPath + "-wal").Length.Should().Be(0);
    }
}
