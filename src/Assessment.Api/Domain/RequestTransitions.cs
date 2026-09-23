namespace Assessment.Api.Domain;

/// <summary>
/// The complete set of legal user actions per request state. Anything not listed here is illegal (409).
///
/// A request's state is its status plus, while PendingApproval, the kind of revision awaiting a decision,
/// because "pending an estimate" and "pending an actual cost" allow different actions.
///
/// Where each action leads (decided in MaintenanceRequest, driven by ApprovalRules):
///
///   Edit              → supersedes any pending content revision; new revision is routed:
///                       needs approval ? PendingApproval : Approved (auto)
///   SubmitActualCost  → supersedes any pending actual; actual ≥ threshold ? PendingApproval : Completed (auto)
///   Approve           → content: Approved     | actual: Completed
///   Reject            → content: Approved with last approved content restored, or Rejected if none ever approved
///                     | actual: Approved (actual discarded; resubmit)
///
/// Raised is transient (routed in the same transaction as creation), so it allows no user actions.
/// Rejected and Completed are terminal.
/// </summary>
public static class RequestTransitions
{
    private static readonly Dictionary<(RequestStatus Status, RevisionKind? Pending), RequestAction[]> Allowed = new()
    {
        [(RequestStatus.Approved, null)] =
            [RequestAction.Edit, RequestAction.SubmitActualCost],

        [(RequestStatus.PendingApproval, RevisionKind.Initial)] =
            [RequestAction.Edit, RequestAction.Approve, RequestAction.Reject],

        [(RequestStatus.PendingApproval, RevisionKind.Edit)] =
            [RequestAction.Edit, RequestAction.Approve, RequestAction.Reject],

        // Work is done: only the actual cost can be corrected; description/estimate are frozen.
        [(RequestStatus.PendingApproval, RevisionKind.ActualCost)] =
            [RequestAction.SubmitActualCost, RequestAction.Approve, RequestAction.Reject],
    };

    public static bool IsAllowed(RequestStatus status, RevisionKind? pendingKind, RequestAction action) =>
        Allowed.TryGetValue((status, pendingKind), out var actions) && actions.Contains(action);

    public static IReadOnlyList<RequestAction> AllowedActions(RequestStatus status, RevisionKind? pendingKind) =>
        Allowed.TryGetValue((status, pendingKind), out var actions) ? actions : [];

    public static void EnsureAllowed(RequestStatus status, RevisionKind? pendingKind, RequestAction action)
    {
        if (!IsAllowed(status, pendingKind, action))
            throw new InvalidTransitionException(status, pendingKind, action);
    }
}
