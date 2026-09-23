using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Assessment.Api.Tests.Persistence;

namespace Assessment.Api.Tests.Http;

// The real flow over HTTP, with real users, as the frontend will drive it. Two of these tests exist because
// a manual curl walkthrough found bugs the domain and persistence tests could not: see comments.
public class RequestLifecycleHttpTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Over_threshold_request_goes_through_estimate_and_actual_approval_with_a_full_history()
    {
        var t = await TestTenant.CreateAsync(factory);
        var requester = await factory.SignedInClientAsync(t.Requester);
        var approver = await factory.SignedInClientAsync(t.Approver);

        var created = await requester.CreateRequestAsync(t.Site.Id, 15_000m);
        Assert.Equal("PendingApproval", created.Status());

        var approved = await (await approver.ApproveAsync(created.Id(), created.PendingRevisionId())).JsonAsync();
        Assert.Equal("Approved", approved.Status());

        // Regression: this adds a NEW revision to a request loaded from the database. With EF's default
        // "Guid keys are store-generated" convention it was sent as an UPDATE and surfaced as a false 409.
        var completing = await (await requester.PostAsJsonAsync(
            $"/api/requests/{created.Id()}/complete", new { actualCost = 12_000m })).JsonAsync();
        Assert.Equal("PendingApproval", completing.Status());

        var completed = await (await approver.ApproveAsync(created.Id(), completing.PendingRevisionId())).JsonAsync();
        Assert.Equal("Completed", completed.Status());
        Assert.Equal(12_000m, completed.GetProperty("actualCost").GetDecimal());

        var history = await requester.GetFromJsonAsync<JsonElement>($"/api/requests/{created.Id()}/history");
        var events = history.GetProperty("events").EnumerateArray()
            .Select(e => (e.GetProperty("action").GetString(), e.GetProperty("actorName").GetString())).ToList();
        Assert.Equal(
        [
            ("Raised", "Rita Requester"), ("SubmittedForApproval", "Rita Requester"), ("Approved", "Andy Approver"),
            ("ActualCostSubmitted", "Rita Requester"), ("SubmittedForApproval", "Rita Requester"), ("Approved", "Andy Approver"),
        ], events);
    }

    [Fact]
    public async Task Below_threshold_request_is_auto_approved_and_attributed_to_the_system()
    {
        var t = await TestTenant.CreateAsync(factory);
        var requester = await factory.SignedInClientAsync(t.Requester);

        var created = await requester.CreateRequestAsync(t.Site.Id, 500m);

        Assert.Equal("Approved", created.Status());
        var revision = created.GetProperty("revisions")[0];
        Assert.Equal("AutoApproved", revision.GetProperty("outcome").GetString());
        Assert.Equal("System", revision.GetProperty("decidedByName").GetString());
    }

    [Fact]
    public async Task Editing_an_approved_request_upward_needs_reapproval()
    {
        var t = await TestTenant.CreateAsync(factory);
        var requester = await factory.SignedInClientAsync(t.Requester);
        var approver = await factory.SignedInClientAsync(t.Approver);
        var created = await requester.CreateRequestAsync(t.Site.Id, 12_000m);
        await (await approver.ApproveAsync(created.Id(), created.PendingRevisionId())).JsonAsync();

        var edited = await (await requester.PutAsJsonAsync($"/api/requests/{created.Id()}",
            new { description = "Fix boiler", estimatedCost = 13_000m, reason = "extra parts" })).JsonAsync();

        Assert.Equal("PendingApproval", edited.Status());
        Assert.Equal(13_000m, edited.GetProperty("pendingRevision").GetProperty("amount").GetDecimal());
    }

    [Fact]
    public async Task Stale_revision_and_illegal_transition_are_conflicts()
    {
        var t = await TestTenant.CreateAsync(factory);
        var requester = await factory.SignedInClientAsync(t.Requester);
        var approver = await factory.SignedInClientAsync(t.Approver);
        var created = await requester.CreateRequestAsync(t.Site.Id, 15_000m);
        var seen = created.PendingRevisionId();

        await requester.PutAsJsonAsync($"/api/requests/{created.Id()}",
            new { description = "Fix boiler", estimatedCost = 16_000m, reason = "more" });

        Assert.Equal(HttpStatusCode.Conflict, (await approver.ApproveAsync(created.Id(), seen)).StatusCode);

        var auto = await requester.CreateRequestAsync(t.Site.Id, 100m); // Approved: nothing to approve
        Assert.Equal(HttpStatusCode.Conflict, (await approver.ApproveAsync(auto.Id(), Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task Malformed_input_is_a_400_never_a_500()
    {
        // Regression: an unparseable Guid in the body used to escape as an unhandled 500.
        var t = await TestTenant.CreateAsync(factory);
        var requester = await factory.SignedInClientAsync(t.Requester);
        var approver = await factory.SignedInClientAsync(t.Approver);
        var created = await requester.CreateRequestAsync(t.Site.Id, 15_000m);

        var badGuid = await approver.PostAsync($"/api/requests/{created.Id()}/approve",
            new StringContent("""{"revisionId":""}""", System.Text.Encoding.UTF8, "application/json"));
        var threeDecimals = await requester.PostAsJsonAsync("/api/requests",
            new { siteId = t.Site.Id, description = "x", estimatedCost = 10.005m });
        var missingReason = await approver.PostAsJsonAsync($"/api/requests/{created.Id()}/reject",
            new { revisionId = created.PendingRevisionId() });
        var badStatusFilter = await requester.GetAsync("/api/requests?status=7");

        Assert.All([badGuid, threeDecimals, missingReason, badStatusFilter],
            r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));
    }

    [Fact]
    public async Task Concurrent_approve_and_reject_exactly_one_wins()
    {
        var t = await TestTenant.CreateAsync(factory);
        var requester = await factory.SignedInClientAsync(t.Requester);
        var approverA = await factory.SignedInClientAsync(t.Approver);
        var approverB = await factory.SignedInClientAsync(t.Approver2);
        var created = await requester.CreateRequestAsync(t.Site.Id, 15_000m);

        var responses = await Task.WhenAll(
            approverA.ApproveAsync(created.Id(), created.PendingRevisionId()),
            approverB.RejectAsync(created.Id(), created.PendingRevisionId()));

        // Whatever the interleaving (xmin conflict, or the loser sees a decided revision), the outcome is one decision.
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], responses.Select(r => r.StatusCode).Order());
    }
}
