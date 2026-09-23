using System.Net;

namespace Assessment.Api.Tests;

// Proves the whole stack is wired: DI, EF Core, Npgsql, migrations, and a live DB connection.
// If this fails, every other test's failure is noise — so it is the first thing worth having.
public class HealthEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Health_returns_healthy_when_database_is_reachable()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}
