using Assessment.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Assessment.Api.Tests.Persistence;

// Forces the exact interleavings that HTTP tests can only hope to hit: two users load the same request,
// both act, both save. The second save must fail, whatever the combination of operations.
public class ConcurrencyTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task Second_of_two_concurrent_decisions_fails()
    {
        var t = await TestTenant.CreateAsync(factory);
        var saved = await t.SaveNewRequestAsync(factory, 15_000m, Now);

        using var a = t.AsApprover(factory);
        using var b = DbScope.As(factory, t.Org.Id, t.Approver2.Id);
        var viaA = await a.Db.MaintenanceRequests.SingleAsync(r => r.Id == saved.Id);
        var viaB = await b.Db.MaintenanceRequests.SingleAsync(r => r.Id == saved.Id);

        viaA.Approve(t.Approver.Id, viaA.PendingRevision!.Id, null, Now);
        viaB.Reject(t.Approver2.Id, viaB.PendingRevision!.Id, "no", Now);

        await a.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => b.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task Revision_only_change_still_conflicts_with_a_concurrent_decision()
    {
        // Resubmitting an actual cost supersedes one revision and adds another but leaves the request row itself
        // unchanged. Without bumping the request row, this would commit alongside a concurrent approval of the
        // superseded revision: an Approved AND Superseded revision, and a pending one on a Completed request.
        var t = await TestTenant.CreateAsync(factory);
        var saved = await t.SaveNewRequestAsync(factory, 5_000m, Now); // auto-approved
        using (var setup = t.AsRequester(factory))
        {
            var r = await setup.Db.MaintenanceRequests.SingleAsync(x => x.Id == saved.Id);
            r.SubmitActualCost(t.Requester.Id, 12_000m, null, Now); // pending actual
            await setup.Db.SaveChangesAsync();
        }

        using var approver = t.AsApprover(factory);
        using var requester = t.AsRequester(factory);
        var viaApprover = await approver.Db.MaintenanceRequests.SingleAsync(r => r.Id == saved.Id);
        var viaRequester = await requester.Db.MaintenanceRequests.SingleAsync(r => r.Id == saved.Id);

        viaApprover.Approve(t.Approver.Id, viaApprover.PendingRevision!.Id, null, Now); // → Completed
        viaRequester.SubmitActualCost(t.Requester.Id, 11_000m, "corrected invoice", Now); // status stays Pending

        await approver.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => requester.Db.SaveChangesAsync());
    }
}
