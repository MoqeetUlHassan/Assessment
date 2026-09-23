using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Assessment.Api.Infrastructure.Data;

/// <summary>
/// Applies EF migrations while holding a Postgres advisory lock, so several processes starting at once
/// (parallel test hosts, or two app instances) migrate one at a time instead of racing. Without it, two hosts
/// on a fresh database both ran the same migration, and one failed dropping an index the other had already dropped.
///
/// The lock is taken on the server's maintenance database ("postgres"), not the target database, because on a
/// brand-new setup the target doesn't exist yet: MigrateAsync is what creates it.
/// </summary>
public static class DatabaseMigrator
{
    public static async Task MigrateAsync(AppDbContext db, CancellationToken ct = default)
    {
        var target = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());
        var lockKey = LockKeyFor(target.Database ?? "");

        var maintenance = new NpgsqlConnectionStringBuilder(target.ConnectionString)
        {
            Database = "postgres",
            Pooling = false, // the lock lives exactly as long as this dedicated connection
        };
        await using var lockConnection = new NpgsqlConnection(maintenance.ConnectionString);
        await lockConnection.OpenAsync(ct);

        await ExecuteAsync(lockConnection, "SELECT pg_advisory_lock(@key)", lockKey, ct);
        try
        {
            await db.Database.MigrateAsync(ct); // later waiters find nothing pending and return immediately
        }
        finally
        {
            await ExecuteAsync(lockConnection, "SELECT pg_advisory_unlock(@key)", lockKey, CancellationToken.None);
        }
    }

    /// <summary>Stable per target database, so migrating the test database never waits on the dev database.</summary>
    private static long LockKeyFor(string databaseName) =>
        BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes($"assessment-migrate:{databaseName}")), 0);

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, long key, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("key", key);
        await command.ExecuteNonQueryAsync(ct);
    }
}
