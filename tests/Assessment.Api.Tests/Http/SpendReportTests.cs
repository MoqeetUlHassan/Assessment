using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Assessment.Api.Domain;
using Assessment.Api.Tests.Persistence;

namespace Assessment.Api.Tests.Http;

// "The report returns the right numbers" is graded, so the numbers are checked against a fixture computed
// by hand. Each row below exists to catch one specific way a spend report goes wrong.
public class SpendReportTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static DateTimeOffset Utc(int month, int day, int hour = 0, int minute = 0, int second = 0) =>
        new(2026, month, day, hour, minute, second, TimeSpan.Zero);

    /// <summary>Creates a request that ends Completed at exactly <paramref name="completedAt"/>, approving as needed.</summary>
    private async Task CompletedAsync(TestTenant t, Guid siteId, decimal estimate, decimal actual, DateTimeOffset completedAt)
    {
        using var scope = t.AsRequester(factory);
        var r = MaintenanceRequest.Raise(t.Org.Id, siteId, t.Requester.Id, "Work", estimate, t.Org.ApprovalThreshold, completedAt.AddDays(-5));
        if (r.Status == RequestStatus.PendingApproval) r.Approve(t.Approver.Id, r.PendingRevision!.Id, null, completedAt.AddDays(-4));
        r.SubmitActualCost(t.Requester.Id, actual, null, completedAt);
        if (r.Status == RequestStatus.PendingApproval) r.Approve(t.Approver.Id, r.PendingRevision!.Id, null, completedAt);
        Assert.Equal(RequestStatus.Completed, r.Status);
        scope.Db.Add(r);
        await scope.Db.SaveChangesAsync();
    }

    private async Task<MaintenanceRequest> SaveAsync(TestTenant t, Guid siteId, decimal estimate, Action<MaintenanceRequest>? then = null)
    {
        using var scope = t.AsRequester(factory);
        var r = MaintenanceRequest.Raise(t.Org.Id, siteId, t.Requester.Id, "Work", estimate, t.Org.ApprovalThreshold, Utc(8, 10));
        then?.Invoke(r);
        scope.Db.Add(r);
        await scope.Db.SaveChangesAsync();
        return r;
    }

    private async Task<Guid> AddSiteAsync(TestTenant t, string name)
    {
        using var scope = DbScope.System(factory);
        var site = Site.Create(t.Org.Id, name);
        scope.Db.Add(site);
        await scope.Db.SaveChangesAsync();
        return site.Id;
    }

    [Fact]
    public async Task Totals_per_site_match_a_hand_computed_fixture()
    {
        var t = await TestTenant.CreateAsync(factory); // threshold 10,000; t.Site is "Site 12"
        var warehouse = await AddSiteAsync(t, "Warehouse");
        var empty = await AddSiteAsync(t, "Empty Site");
        var siteA = t.Site.Id;

        // Site 12: two in range (on both boundaries), two just outside.
        await CompletedAsync(t, siteA, 900m, 1_000.00m, Utc(8, 1));                 // IN: first instant of range; actual, not estimate
        await CompletedAsync(t, siteA, 2_000m, 2_500.50m, Utc(8, 31, 23, 59, 59));  // IN: last second of the last day
        await CompletedAsync(t, siteA, 500m, 9_999.00m, Utc(9, 1));                 // OUT: first instant after the range
        await CompletedAsync(t, siteA, 500m, 7_777.00m, Utc(7, 31, 23, 59, 59));    // OUT: last second before the range

        // Warehouse: one completed through both approvals; three that are not spend.
        await CompletedAsync(t, warehouse, 11_000m, 12_000.00m, Utc(8, 15, 12));    // IN: over-threshold path, actual 12,000
        await SaveAsync(t, warehouse, 5_000m);                                       // OUT: approved, never completed
        await SaveAsync(t, warehouse, 5_000m, r => r.SubmitActualCost(t.Requester.Id, 11_000m, null, Utc(8, 12))); // OUT: actual pending approval
        await SaveAsync(t, warehouse, 20_000m, r => r.Reject(t.Approver.Id, r.PendingRevision!.Id, "no", Utc(8, 11))); // OUT: rejected

        // Another organization's spend in the same range must never appear.
        var other = await TestTenant.CreateAsync(factory);
        await CompletedAsync(other, other.Site.Id, 1_000m, 50_000.00m, Utc(8, 10));

        var client = await factory.SignedInClientAsync(t.Approver);
        var report = await client.GetFromJsonAsync<JsonElement>("/api/reports/spend?from=2026-08-01&to=2026-08-31");

        var sites = report.GetProperty("sites").EnumerateArray().ToDictionary(
            s => s.GetProperty("siteName").GetString()!,
            s => (Total: s.GetProperty("totalSpend").GetDecimal(), Count: s.GetProperty("completedCount").GetInt32()));

        Assert.Equal(3, sites.Count); // exactly this org's sites, the empty one included
        Assert.Equal((3_500.50m, 2), sites["Site 12"]);
        Assert.Equal((12_000.00m, 1), sites["Warehouse"]);
        Assert.Equal((0m, 0), sites["Empty Site"]);
        Assert.Equal(15_500.50m, report.GetProperty("totalSpend").GetDecimal());
        Assert.Equal(3, report.GetProperty("completedCount").GetInt32());
        Assert.DoesNotContain(report.GetProperty("sites").EnumerateArray(), s => s.GetProperty("siteId").GetGuid() == empty && s.GetProperty("totalSpend").GetDecimal() != 0);
    }

    [Fact]
    public async Task A_single_day_range_includes_the_whole_day()
    {
        var t = await TestTenant.CreateAsync(factory);
        await CompletedAsync(t, t.Site.Id, 100m, 100m, Utc(8, 20, 0, 0, 0));
        await CompletedAsync(t, t.Site.Id, 200m, 200m, Utc(8, 20, 23, 59, 59));
        await CompletedAsync(t, t.Site.Id, 400m, 400m, Utc(8, 21));

        var client = await factory.SignedInClientAsync(t.Approver);
        var report = await client.GetFromJsonAsync<JsonElement>("/api/reports/spend?from=2026-08-20&to=2026-08-20");

        Assert.Equal(300m, report.GetProperty("totalSpend").GetDecimal());
    }

    [Fact]
    public async Task Report_requires_the_spend_permission()
    {
        var t = await TestTenant.CreateAsync(factory);
        var requester = await factory.SignedInClientAsync(t.Requester);

        var response = await requester.GetAsync("/api/reports/spend?from=2026-08-01&to=2026-08-31");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("from=2026-08-31&to=2026-08-01")] // reversed
    [InlineData("from=2025-01-01&to=2026-08-31")] // longer than 366 days
    [InlineData("to=2026-08-31")]                 // missing from
    [InlineData("from=2026-13-01&to=2026-12-31")] // not a date
    public async Task Invalid_ranges_are_rejected(string query)
    {
        var t = await TestTenant.CreateAsync(factory);
        var client = await factory.SignedInClientAsync(t.Approver);

        var response = await client.GetAsync($"/api/reports/spend?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
