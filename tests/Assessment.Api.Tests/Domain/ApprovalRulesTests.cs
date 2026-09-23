using Assessment.Api.Domain;
using static Assessment.Api.Tests.Domain.RequestBuilder;

namespace Assessment.Api.Tests.Domain;

// The threshold rules are the core business logic, and off-by-one at the boundary is the likeliest bug.
// Tested through the aggregate (not ApprovalRules alone) so routing, not just the formula, is covered.
public class ApprovalRulesTests
{
    [Theory]
    [InlineData(9_999.99, RequestStatus.Approved)]
    [InlineData(10_000.00, RequestStatus.PendingApproval)] // equal to threshold needs approval
    [InlineData(10_000.01, RequestStatus.PendingApproval)]
    public void Initial_estimate_is_routed_by_threshold(decimal estimate, RequestStatus expected)
    {
        Assert.Equal(expected, Raise(estimate).Status);
    }

    [Theory]
    // approved at 12k (manually); edits judged against that approval
    [InlineData(11_000, false, RequestStatus.Approved)]        // decrease, same wording → auto
    [InlineData(12_000, true, RequestStatus.PendingApproval)]  // same cost, new wording, above threshold → re-approve
    [InlineData(13_000, false, RequestStatus.PendingApproval)] // increase above threshold → re-approve
    [InlineData(9_000, true, RequestStatus.Approved)]          // new wording but below threshold → auto
    public void Edit_of_an_approved_request_needs_approval_only_for_new_asks_at_or_above_threshold(
        decimal newEstimate, bool changeDescription, RequestStatus expected)
    {
        var request = Approved(Raise(12_000m));

        Edited(request, newEstimate, changeDescription ? "Replace boiler entirely" : null);

        Assert.Equal(expected, request.Status);
    }

    [Fact]
    public void Increase_that_stays_below_threshold_is_auto_approved()
    {
        var request = Edited(Raise(5_000m), 9_000m);
        Assert.Equal(RequestStatus.Approved, request.Status);
        Assert.Equal(RevisionOutcome.AutoApproved, request.Revisions[^1].Outcome);
    }

    [Theory]
    [InlineData(9_999.99, RequestStatus.Completed)]
    [InlineData(10_000.00, RequestStatus.PendingApproval)]
    public void Actual_cost_at_or_above_threshold_blocks_completion(decimal actual, RequestStatus expected)
    {
        // Even when the actual is within the approved estimate: decision 7 says >= threshold always needs sign-off.
        var request = Approved(Raise(20_000m));

        WithActual(request, actual);

        Assert.Equal(expected, request.Status);
        Assert.Equal(expected == RequestStatus.Completed ? actual : null, request.ActualCost);
    }

    [Fact]
    public void Request_keeps_the_threshold_it_was_created_under()
    {
        var org = Organization.Create("Acme", approvalThreshold: 20_000m);
        var request = MaintenanceRequest.Raise(org.Id, SiteId, Requester, "Fix roof", 15_000m, org.ApprovalThreshold, Now);

        org.ChangeApprovalThreshold(10_000m, Requester, "tighten controls", Now);
        Edited(request, 16_000m);

        // Judged against the 20k snapshot, not the org's new 10k.
        Assert.Equal(20_000m, request.ApprovalThreshold);
        Assert.Equal(RequestStatus.Approved, request.Status);
    }
}
