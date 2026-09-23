namespace Assessment.Api.Domain;

/// <summary>
/// Immutable snapshot of what is being paid for: the initial request, each edit, each actual cost.
/// Only the outcome fields change, and only once (Pending → decided).
/// </summary>
public class RequestRevision : Entity, ITenantOwned
{
    public Guid OrganizationId { get; private init; }
    public Guid RequestId { get; private init; }

    /// <summary>1, 2, 3… per request. Orders revisions without relying on clock or Guid ordering.</summary>
    public int Sequence { get; private init; }

    public RevisionKind Kind { get; private init; }
    public string Description { get; private init; } = null!;

    /// <summary>The estimate for Initial/Edit revisions; the actual cost for ActualCost revisions.</summary>
    public decimal Amount { get; private init; }

    public Guid? PreviousRevisionId { get; private init; }
    public string? Reason { get; private init; }
    public Guid SubmittedById { get; private init; }

    public RevisionOutcome Outcome { get; private set; } = RevisionOutcome.Pending;

    /// <summary>Null with outcome AutoApproved means the system decided.</summary>
    public Guid? DecidedById { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public string? DecisionComment { get; private set; }

    public bool IsApprovedContent =>
        Kind != RevisionKind.ActualCost && Outcome is RevisionOutcome.Approved or RevisionOutcome.AutoApproved;

    private RequestRevision() { }

    internal static RequestRevision Create(
        MaintenanceRequest request, int sequence, RevisionKind kind, string description, decimal amount,
        Guid submittedById, string? reason, Guid? previousRevisionId) => new()
    {
        OrganizationId = request.OrganizationId,
        RequestId = request.Id,
        Sequence = sequence,
        Kind = kind,
        Description = description,
        Amount = amount,
        SubmittedById = submittedById,
        Reason = reason,
        PreviousRevisionId = previousRevisionId,
    };

    internal void AutoApprove(DateTimeOffset now) => Decide(RevisionOutcome.AutoApproved, null, null, now);

    internal void Approve(Guid approverId, string? comment, DateTimeOffset now) =>
        Decide(RevisionOutcome.Approved, approverId, comment, now);

    internal void Reject(Guid approverId, string reason, DateTimeOffset now) =>
        Decide(RevisionOutcome.Rejected, approverId, reason, now);

    internal void Supersede(DateTimeOffset now) => Decide(RevisionOutcome.Superseded, null, null, now);

    private void Decide(RevisionOutcome outcome, Guid? deciderId, string? comment, DateTimeOffset now)
    {
        if (Outcome != RevisionOutcome.Pending)
            throw new DomainException($"Revision {Id} was already {Outcome}.");
        Outcome = outcome;
        DecidedById = deciderId;
        DecidedAt = now;
        DecisionComment = comment;
    }
}
