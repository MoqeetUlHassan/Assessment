using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Assessment.Api.Tests.Persistence;

namespace Assessment.Api.Tests.Http;

// "Hiding a button is not authorization": every rule below is exercised by calling the API directly,
// the way someone with curl and a valid session would. And every cross-tenant case uses REAL ids.
public class RequestAuthorizationHttpTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Self_approval_is_refused_for_requester_approver_and_orgadmin()
    {
        var t = await TestTenant.CreateAsync(factory);
        var approver = await factory.SignedInClientAsync(t.Approver);

        foreach (var user in new[] { t.Requester, t.Approver, t.Admin })
        {
            var client = await factory.SignedInClientAsync(user);
            var own = await client.CreateRequestAsync(t.Site.Id, 15_000m);

            var response = await client.ApproveAsync(own.Id(), own.PendingRevisionId());

            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{user.DisplayName} approved their own request");
        }
    }

    [Fact]
    public async Task Requester_cannot_edit_or_complete_someone_elses_request_but_manage_permission_can()
    {
        var t = await TestTenant.CreateAsync(factory);
        var requester = await factory.SignedInClientAsync(t.Requester);
        var approver = await factory.SignedInClientAsync(t.Approver);
        var approver2 = await factory.SignedInClientAsync(t.Approver2);
        var theirs = await approver.CreateRequestAsync(t.Site.Id, 500m); // auto-approved

        var edit = new { description = "Changed", estimatedCost = 600m, reason = "because" };
        Assert.Equal(HttpStatusCode.Forbidden, (await requester.PutAsJsonAsync($"/api/requests/{theirs.Id()}", edit)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await requester.PostAsJsonAsync($"/api/requests/{theirs.Id()}/complete", new { actualCost = 100m })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await approver2.PutAsJsonAsync($"/api/requests/{theirs.Id()}", edit)).StatusCode);
    }

    [Fact]
    public async Task Requester_without_approve_permission_gets_403_even_for_others_requests()
    {
        var t = await TestTenant.CreateAsync(factory);
        var approver = await factory.SignedInClientAsync(t.Approver);
        var requester = await factory.SignedInClientAsync(t.Requester);
        var theirs = await approver.CreateRequestAsync(t.Site.Id, 15_000m);

        Assert.Equal(HttpStatusCode.Forbidden, (await requester.ApproveAsync(theirs.Id(), theirs.PendingRevisionId())).StatusCode);
    }

    [Fact]
    public async Task Every_request_endpoint_returns_404_for_another_organizations_request()
    {
        var victim = await TestTenant.CreateAsync(factory);
        var victimClient = await factory.SignedInClientAsync(victim.Requester);
        var target = await victimClient.CreateRequestAsync(victim.Site.Id, 15_000m);
        var id = target.Id();
        var revisionId = target.PendingRevisionId();

        var attacker = await TestTenant.CreateAsync(factory);
        // The most privileged attacker possible: an OrgAdmin holding every permission.
        var client = await factory.SignedInClientAsync(attacker.Admin);

        var attempts = new Dictionary<string, HttpResponseMessage>
        {
            ["GET"] = await client.GetAsync($"/api/requests/{id}"),
            ["history"] = await client.GetAsync($"/api/requests/{id}/history"),
            ["edit"] = await client.PutAsJsonAsync($"/api/requests/{id}", new { description = "x", estimatedCost = 1m, reason = "x" }),
            ["complete"] = await client.PostAsJsonAsync($"/api/requests/{id}/complete", new { actualCost = 1m }),
            ["approve"] = await client.ApproveAsync(id, revisionId),
            ["reject"] = await client.RejectAsync(id, revisionId),
        };

        Assert.All(attempts, a => Assert.True(a.Value.StatusCode == HttpStatusCode.NotFound, $"{a.Key} returned {a.Value.StatusCode}"));

        // And nothing changed on the victim's side.
        var after = await victimClient.GetFromJsonAsync<JsonElement>($"/api/requests/{id}");
        Assert.Equal("PendingApproval", after.Status());
        Assert.Single(after.GetProperty("revisions").EnumerateArray());
    }

    [Fact]
    public async Task Another_organizations_site_cannot_be_used_and_their_requests_never_appear_in_lists()
    {
        var victim = await TestTenant.CreateAsync(factory);
        var victimClient = await factory.SignedInClientAsync(victim.Requester);
        var theirRequest = await victimClient.CreateRequestAsync(victim.Site.Id, 500m);

        var attacker = await TestTenant.CreateAsync(factory);
        var client = await factory.SignedInClientAsync(attacker.Admin);

        var create = await client.PostAsJsonAsync("/api/requests",
            new { siteId = victim.Site.Id, description = "planted", estimatedCost = 10m });
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);

        var list = await client.GetFromJsonAsync<JsonElement>($"/api/requests?siteId={victim.Site.Id}");
        Assert.Equal(0, list.GetProperty("totalCount").GetInt32());
        var sites = await client.GetFromJsonAsync<JsonElement>("/api/sites");
        Assert.DoesNotContain(sites.EnumerateArray(), s => s.GetProperty("id").GetGuid() == victim.Site.Id);
        Assert.NotEqual(Guid.Empty, theirRequest.Id());
    }

    [Fact]
    public async Task Available_actions_reflect_the_callers_rights_without_granting_anything()
    {
        var t = await TestTenant.CreateAsync(factory);
        var requester = await factory.SignedInClientAsync(t.Requester);
        var approver = await factory.SignedInClientAsync(t.Approver);
        var created = await requester.CreateRequestAsync(t.Site.Id, 15_000m);

        var asRequester = (await requester.GetFromJsonAsync<JsonElement>($"/api/requests/{created.Id()}")).GetProperty("actions");
        var asApprover = (await approver.GetFromJsonAsync<JsonElement>($"/api/requests/{created.Id()}")).GetProperty("actions");

        Assert.False(asRequester.GetProperty("canApprove").GetBoolean());
        Assert.True(asApprover.GetProperty("canApprove").GetBoolean());
    }
}
