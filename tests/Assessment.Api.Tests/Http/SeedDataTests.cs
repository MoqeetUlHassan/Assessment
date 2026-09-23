using System.Net.Http.Json;
using System.Text.Json;
using Assessment.Api.Infrastructure.Data;

namespace Assessment.Api.Tests.Http;

// The README tells reviewers to log in with the seeded accounts. If this breaks, the "run it in
// 15 minutes" experience breaks, so the documented credentials are tested exactly as written.
public class SeedDataTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Theory]
    [InlineData("admin@acme.test", "OrgAdmin", "Acme Retail")]
    [InlineData("approver1@acme.test", "Approver", "Acme Retail")]
    [InlineData("requester@globex.test", "Requester", "Globex Offices")]
    public async Task Documented_seed_accounts_can_log_in(string email, string role, string org)
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password = DevelopmentSeeder.Password });

        response.EnsureSuccessStatusCode();
        var me = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(role, me.GetProperty("role").GetString());
        Assert.Equal(org, me.GetProperty("organization").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Seeding_is_idempotent()
    {
        // The factory already seeded on startup; a second run must not duplicate or throw.
        await DevelopmentSeeder.SeedAsync(factory.Services);

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new { email = "admin@acme.test", password = DevelopmentSeeder.Password });
        response.EnsureSuccessStatusCode();
    }
}
