namespace Assessment.Api.Domain;

public enum RequestStatus
{
    /// <summary>Transient: set on creation and routed in the same transaction. Visible only in the audit trail.</summary>
    Raised,
    PendingApproval,
    Approved,
    Rejected,
    Completed,
}

public enum RevisionKind
{
    Initial,
    Edit,
    ActualCost,
}

public enum RevisionOutcome
{
    Pending,
    AutoApproved,
    Approved,
    Rejected,
    Superseded,
}

/// <summary>Actions a user can take on an existing request. Creation is not an action on a request.</summary>
public enum RequestAction
{
    Edit,
    SubmitActualCost,
    Approve,
    Reject,
}
