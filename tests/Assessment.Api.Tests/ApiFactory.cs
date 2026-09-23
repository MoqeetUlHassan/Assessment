using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Assessment.Api.Tests;

/// <summary>
/// Boots the real app against a real Postgres database (not an in-memory fake),
/// so tests exercise the same provider, SQL and migrations as production.
/// Override with TEST_CONNECTION_STRING; defaults to a separate *_test database.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("TEST_CONNECTION_STRING")
        ?? "Host=localhost;Port=5432;Database=assessment_test;Username=postgres;Password=postgres";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Default", ConnectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
    }
}
