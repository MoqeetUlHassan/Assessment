using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Assessment.Api.Tests.Persistence;

namespace Assessment.Api.Tests.Http;

// Anyone in an organization can add a site; the site must belong to THAT organization no matter what the
// client sends, and must never be visible to, or collide with, another organization's sites.
public class SiteTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static async Task<JsonElement> AddSiteAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/sites", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<List<string>> SiteNamesAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/sites")).EnumerateArray()
            .Select(s => s.GetProperty("name").GetString()!).ToList();

    [Fact]
    public async Task A_requester_can_add_a_site_and_raise_a_request_on_it()
    {
        var t = await TestTenant.CreateAsync(factory);
        var requester = await factory.SignedInClientAsync(t.Requester); // lowest role: requests.create only

        var site = await AddSiteAsync(requester, "  Warehouse East  ");

        Assert.Equal("Warehouse East", site.GetProperty("name").GetString()); // trimmed
        Assert.Contains("Warehouse East", await SiteNamesAsync(requester));
        var request = await requester.CreateRequestAsync(site.GetProperty("id").GetGuid(), 500m);
        Assert.Equal("Approved", request.Status());
    }

    [Fact]
    public async Task A_new_site_belongs_to_the_callers_organization_whatever_the_body_says()
    {
        var mine = await TestTenant.CreateAsync(factory);
        var other = await TestTenant.CreateAsync(factory);
        var client = await factory.SignedInClientAsync(mine.Requester);

        // organizationId is not part of the contract and must be ignored.
        var response = await client.PostAsJsonAsync("/api/sites", new { name = "Planted Site", organizationId = other.Org.Id });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        Assert.Contains("Planted Site", await SiteNamesAsync(client));
        var otherClient = await factory.SignedInClientAsync(other.Requester);
        Assert.DoesNotContain("Planted Site", await SiteNamesAsync(otherClient));

        // And the other organization can't raise a request against it (it doesn't exist for them).
        var siteId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var foreign = await otherClient.PostAsJsonAsync("/api/requests", new { siteId, description = "x", estimatedCost = 10m });
        Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
    }

    [Fact]
    public async Task Names_are_unique_within_an_organization_case_insensitively_but_not_across_organizations()
    {
        var t = await TestTenant.CreateAsync(factory); // already has "Site 12"
        var other = await TestTenant.CreateAsync(factory);
        var client = await factory.SignedInClientAsync(t.Approver);

        var duplicate = await client.PostAsJsonAsync("/api/sites", new { name = "site 12" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        // Another organization adding a name this org already uses is fine: names don't cross tenants.
        await AddSiteAsync(await factory.SignedInClientAsync(other.Requester), "Only Here");
        await AddSiteAsync(client, "Only Here");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_or_over_long_names_are_rejected(string name)
    {
        var t = await TestTenant.CreateAsync(factory);
        var client = await factory.SignedInClientAsync(t.Requester);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/sites", new { name })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync("/api/sites", new { name = new string('x', 201) })).StatusCode);
    }

    [Fact]
    public async Task Adding_a_site_requires_a_session_and_is_audited_with_the_actor()
    {
        var t = await TestTenant.CreateAsync(factory);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await factory.CreateClient().PostAsJsonAsync("/api/sites", new { name = "Anonymous" })).StatusCode);

        await AddSiteAsync(await factory.SignedInClientAsync(t.Requester), "Audited Site");

        var admin = await factory.SignedInClientAsync(t.Admin);
        var log = await admin.GetFromJsonAsync<JsonElement>("/api/admin/audit?entityType=Site");
        var entry = Assert.Single(log.GetProperty("items").EnumerateArray());
        Assert.Equal("SiteCreated", entry.GetProperty("action").GetString());
        Assert.Equal("Rita Requester", entry.GetProperty("actorName").GetString());
        Assert.Contains("Audited Site", entry.GetProperty("details").GetRawText());
    }
}
