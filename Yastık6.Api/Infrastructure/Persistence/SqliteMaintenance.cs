using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Yastık6.Api.Infrastructure.Persistence;

public static class SqliteMaintenance
{
    // EF Core's Migrate() takes a lock row and waits forever if a previous process was killed while holding it.
    // There is a single app instance, so any row present at startup is stale.
    public static int ClearStaleMigrationLock(DbContext context)
    {
        try
        {
            return context.Database.ExecuteSqlRaw("DELETE FROM __EFMigrationsLock;");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            return 0; // Fresh database: the lock table does not exist yet.
        }
    }

    // Folds the WAL back into the main file and truncates it. Busy=1 means readers prevented a full checkpoint.
    public static (bool Busy, int WalFrames, int CheckpointedFrames) CheckpointWal(DbContext context)
    {
        var connection = context.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed) connection.Open();

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using var reader = command.ExecuteReader();
            reader.Read();
            return (reader.GetInt32(0) == 1, reader.GetInt32(1), reader.GetInt32(2));
        }
        finally
        {
            if (wasClosed) connection.Close();
        }
    }
}
