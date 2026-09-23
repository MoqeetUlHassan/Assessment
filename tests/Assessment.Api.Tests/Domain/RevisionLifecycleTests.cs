using Assessment.Api.Domain;
using static Assessment.Api.Tests.Domain.RequestBuilder;

namespace Assessment.Api.Tests.Domain;

// Behaviours that follow from the review decisions: supersede-on-edit, "approve what you saw",
// and where a rejection lands. These are the paths most likely to be subtly wrong.
public class RevisionLifecycleTests
{
    [Fact]
    public void Editing_while_pending_supersedes_and_the_old_revision_can_no_longer_be_approved()
    {
        var request = Raise(15_000m);
        var seenByApprover = request.PendingRevision!.Id;

        Edited(request, 18_000m);

        Assert.Equal(RevisionOutcome.Superseded, request.Revisions.Single(r => r.Id == seenByApprover).Outcome);
        Assert.Throws<StaleRevisionException>(() => request.Approve(Approver, seenByApprover, null, Now));
        Assert.Equal(RequestStatus.PendingApproval, request.Status);
    }

    [Fact]
    public void Rejecting_an_edit_restores_the_last_approved_content()
    {
        var request = Approved(Raise(12_000m, "Fix boiler"));
        Edited(request, 14_000m, "Replace boiler");

        Rejected(request);

        Assert.Equal(RequestStatus.Approved, request.Status);
        Assert.Equal(12_000m, request.EstimatedCost);
        Assert.Equal("Fix boiler", request.Description);
    }

    [Fact]
    public void Rejecting_a_request_that_was_never_approved_is_terminal()
    {
        var request = Rejected(Raise(15_000m));
        Assert.Equal(RequestStatus.Rejected, request.Status);
    }

    [Fact]
    public void Rejecting_an_actual_cost_returns_to_approved_without_recording_it()
    {
        var request = WithActual(Raise(5_000m), 12_000m);

        Rejected(request);

        Assert.Equal(RequestStatus.Approved, request.Status);
        Assert.Null(request.ActualCost);
        Assert.Null(request.CompletedAt);
    }

    [Fact]
    public void Approving_an_actual_cost_completes_with_that_amount()
    {
        var request = Approved(WithActual(Raise(5_000m), 12_000m));

        Assert.Equal(RequestStatus.Completed, request.Status);
        Assert.Equal(12_000m, request.ActualCost);
        Assert.Equal(Now, request.CompletedAt);
    }

    [Fact]
    public void Edit_that_changes_nothing_is_refused()
    {
        var request = Raise(5_000m);
        Assert.Throws<DomainException>(() => request.Edit(Requester, request.Description, request.EstimatedCost, "x", Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10.001)]
    [InlineData(10_000_000.01)]
    public void Invalid_amounts_are_refused(decimal amount)
    {
        Assert.Throws<DomainException>(() => Raise(amount));
    }
}
