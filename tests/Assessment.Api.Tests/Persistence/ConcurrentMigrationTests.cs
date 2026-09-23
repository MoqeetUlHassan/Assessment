using Assessment.Api.Infrastructure.Data;
using Assessment.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Assessment.Api.Tests.Persistence;

// Regression: on a brand-new database, parallel test hosts all ran MigrateAsync at once and one failed
// ("index ... does not exist") when two dropped the same index. A first-time reviewer running `dotnet test`
// hit it every time. This reproduces the race directly against a fresh, throwaway database.
public class ConcurrentMigrationTests
{
    [Fact]
    public async Task Parallel_migrations_on_a_brand_new_database_all_succeed()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(ApiFactory.ConnectionString)
            { Database = $"assessment_migrate_{Guid.NewGuid():N}" }.ConnectionString;
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                await using var db = NewContext(connectionString);
                await DatabaseMigrator.MigrateAsync(db);
            })));

            await using var check = NewContext(connectionString);
            Assert.Empty(await check.Database.GetPendingMigrationsAsync());
            Assert.Equal(check.Database.GetMigrations(), await check.Database.GetAppliedMigrationsAsync());
        }
        finally
        {
            await DropDatabaseAsync(connectionString);
        }
    }

    private static AppDbContext NewContext(string connectionString) => new(
        new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).UseSnakeCaseNamingConvention().Options,
        new TenantContext());

    private static async Task DropDatabaseAsync(string connectionString)
    {
        var target = new NpgsqlConnectionStringBuilder(connectionString);
        var maintenance = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres", Pooling = false };
        await using var connection = new NpgsqlConnection(maintenance.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{target.Database}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}
