using Assessment.Api.Domain;
using static Assessment.Api.Tests.Domain.RequestBuilder;

namespace Assessment.Api.Tests.Domain;

// Compliance: every state change must be recorded with who/what/when. These check the domain produces
// the right events; persistence atomicity and immutability are tested against Postgres in step 2.
public class AuditTrailTests
{
    [Fact]
    public void Auto_approval_is_recorded_as_the_system_acting_on_behalf_of_the_requester()
    {
        var request = Raise(5_000m);

        var events = request.PendingAuditEvents;
        Assert.Equal([AuditActions.Raised, AuditActions.AutoApproved], events.Select(e => e.Action));

        var auto = events[1];
        Assert.Null(auto.ActorUserId);
        Assert.Equal(Requester, auto.Details["triggeredBy"]);
        Assert.Equal(("Raised", "Approved"), (auto.FromStatus, auto.ToStatus));
    }

    [Fact]
    public void Manual_decision_records_the_approver()
    {
        var request = Approved(Raise(15_000m));

        var approved = request.PendingAuditEvents[^1];
        Assert.Equal(AuditActions.Approved, approved.Action);
        Assert.Equal(Approver, approved.ActorUserId);
        Assert.Equal(("PendingApproval", "Approved"), (approved.FromStatus, approved.ToStatus));
    }

    [Fact]
    public void Replaying_the_status_changes_in_the_audit_trail_reproduces_the_current_status()
    {
        // A long, messy path: pending → superseded edit → approved → overrun pending → rejected → resubmitted → approved.
        var request = Raise(15_000m);
        Edited(request, 16_000m);
        Approved(request);
        WithActual(request, 20_000m);
        Rejected(request);
        WithActual(request, 19_000m);
        Approved(request);

        var changes = request.PendingAuditEvents.Where(e => e.ToStatus is not null).ToList();

        // Each change starts where the previous one ended: no status change went unrecorded.
        for (var i = 1; i < changes.Count; i++)
            Assert.Equal(changes[i - 1].ToStatus, changes[i].FromStatus);
        Assert.Equal(request.Status.ToString(), changes[^1].ToStatus);
        Assert.All(request.PendingAuditEvents, e => Assert.Equal(Org, e.OrganizationId));
    }
}
