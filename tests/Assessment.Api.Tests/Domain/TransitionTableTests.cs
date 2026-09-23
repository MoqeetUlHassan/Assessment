using Assessment.Api.Domain;
using static Assessment.Api.Tests.Domain.RequestBuilder;

namespace Assessment.Api.Tests.Domain;

// The brief says "not every transition is legal; enforce that". Happy-path tests would only prove the
// legal half. This drives a real request into every reachable state and tries EVERY action, so an
// accidentally-permitted transition fails here. The expected table is restated independently of
// RequestTransitions (it is the table reviewed by hand) so the test is not a copy of the code.
public class TransitionTableTests
{
    private static readonly Dictionary<string, RequestAction[]> Expected = new()
    {
        ["Approved"] = [RequestAction.Edit, RequestAction.SubmitActualCost],
        ["Pending(Initial)"] = [RequestAction.Edit, RequestAction.Approve, RequestAction.Reject],
        ["Pending(Edit)"] = [RequestAction.Edit, RequestAction.Approve, RequestAction.Reject],
        ["Pending(ActualCost)"] = [RequestAction.SubmitActualCost, RequestAction.Approve, RequestAction.Reject],
        ["Rejected"] = [],
        ["Completed"] = [],
    };

    public static TheoryData<string, RequestAction> EveryStateAndAction()
    {
        var data = new TheoryData<string, RequestAction>();
        foreach (var state in Expected.Keys)
        foreach (var action in Enum.GetValues<RequestAction>())
            data.Add(state, action);
        return data;
    }

    [Theory]
    [MemberData(nameof(EveryStateAndAction))]
    public void Action_is_allowed_only_where_the_table_says(string state, RequestAction action)
    {
        var request = InState(state);
        var shouldBeAllowed = Expected[state].Contains(action);

        var ex = Record.Exception(() => Perform(request, action));

        if (shouldBeAllowed)
            Assert.IsNotType<InvalidTransitionException>(ex); // null, or unrelated to legality
        else
            Assert.IsType<InvalidTransitionException>(ex);
    }

    [Fact]
    public void Raised_is_never_observable_after_creation()
    {
        Assert.NotEqual(RequestStatus.Raised, Raise(5_000m).Status);
        Assert.NotEqual(RequestStatus.Raised, Raise(50_000m).Status);
    }

    private static void Perform(MaintenanceRequest r, RequestAction action)
    {
        // A pending revision id where one exists, so Approve/Reject fail only on legality, not staleness.
        var revisionId = r.PendingRevision?.Id ?? Guid.CreateVersion7();
        switch (action)
        {
            case RequestAction.Edit: r.Edit(Requester, "Replace boiler", 7_000m, "scope", Now); break;
            case RequestAction.SubmitActualCost: r.SubmitActualCost(Requester, 800m, null, Now); break;
            case RequestAction.Approve: r.Approve(Approver, revisionId, null, Now); break;
            case RequestAction.Reject: r.Reject(Approver, revisionId, "no", Now); break;
            default: throw new ArgumentOutOfRangeException(nameof(action));
        }
    }
}
