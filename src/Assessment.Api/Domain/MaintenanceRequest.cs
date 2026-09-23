namespace Assessment.Api.Domain;

/// <summary>
/// Aggregate root for a maintenance request and its revisions.
///
/// Owns state legality (RequestTransitions) and approval routing (ApprovalRules).
/// Does NOT decide who may act: permissions and conflict-of-interest rules (no approving your own request
/// or a revision you submitted) are enforced by the authorization handlers before these methods are called,
/// and backed by a DB CHECK on request_revisions.
///
/// Every state change raises an AuditEvent that is saved in the same transaction.
/// </summary>
public class MaintenanceRequest : Entity, ITenantOwned
{
    private readonly List<RequestRevision> _revisions = [];

    public Guid OrganizationId { get; private init; }
    public Guid SiteId { get; private init; }
    public Guid RequestedById { get; private init; }

    /// <summary>Org threshold snapshotted at creation. Governs every revision of this request.</summary>
    public decimal ApprovalThreshold { get; private init; }

    public RequestStatus Status { get; private set; }

    /// <summary>Current content, which may be awaiting approval. See LastApprovedContent for what was signed off.</summary>
    public string Description { get; private set; } = null!;
    public decimal EstimatedCost { get; private set; }

    public decimal? ActualCost { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    public IReadOnlyList<RequestRevision> Revisions => _revisions;

    public RequestRevision? PendingRevision => _revisions.SingleOrDefault(r => r.Outcome == RevisionOutcome.Pending);

    /// <summary>The most recent content revision that was approved (manually or automatically), if any.</summary>
    public RequestRevision? LastApprovedContent =>
        _revisions.Where(r => r.IsApprovedContent).MaxBy(r => r.Sequence);

    public IReadOnlyList<RequestAction> AllowedActions =>
        RequestTransitions.AllowedActions(Status, PendingRevision?.Kind);

    private MaintenanceRequest() { }

    public static MaintenanceRequest Raise(
        Guid organizationId, Guid siteId, Guid requesterId, string description, decimal estimatedCost,
        decimal organizationThreshold, DateTimeOffset now)
    {
        var validDescription = Guard.Description(description);
        var validCost = Guard.Amount(estimatedCost, "Estimated cost");

        var request = new MaintenanceRequest
        {
            OrganizationId = organizationId,
            SiteId = siteId,
            RequestedById = requesterId,
            ApprovalThreshold = organizationThreshold,
            Status = RequestStatus.Raised,
            Description = validDescription,
            EstimatedCost = validCost,
        };

        request.Audit(AuditActions.Raised, requesterId, now, new()
        {
            ["siteId"] = siteId,
            ["description"] = validDescription,
            ["estimatedCost"] = validCost,
            ["threshold"] = organizationThreshold,
        }, toStatus: RequestStatus.Raised);

        var revision = request.AddRevision(RevisionKind.Initial, validDescription, validCost, requesterId, null, null);
        request.RouteContent(revision, requesterId, now);
        return request;
    }

    public void Edit(Guid actorId, string description, decimal estimatedCost, string reason, DateTimeOffset now)
    {
        EnsureAllowed(RequestAction.Edit);
        var validDescription = Guard.Description(description);
        var validCost = Guard.Amount(estimatedCost, "Estimated cost");
        var validReason = Guard.Reason(reason);

        if (validDescription == Description && validCost == EstimatedCost)
            throw new DomainException("Nothing changed.");

        var replaced = SupersedePending(actorId, now) ?? LastApprovedContent;
        var revision = AddRevision(RevisionKind.Edit, validDescription, validCost, actorId, validReason, replaced?.Id);

        Audit(AuditActions.Edited, actorId, now, new()
        {
            ["revisionId"] = revision.Id,
            ["descriptionBefore"] = Description,
            ["descriptionAfter"] = validDescription,
            ["estimatedCostBefore"] = EstimatedCost,
            ["estimatedCostAfter"] = validCost,
            ["reason"] = validReason,
        });

        RouteContent(revision, actorId, now);
    }

    public void SubmitActualCost(Guid actorId, decimal actualCost, string? reason, DateTimeOffset now)
    {
        EnsureAllowed(RequestAction.SubmitActualCost);
        var validActual = Guard.Amount(actualCost, "Actual cost");
        var validReason = Guard.OptionalText(reason, "Reason");

        var replaced = SupersedePending(actorId, now);
        var revision = AddRevision(RevisionKind.ActualCost, Description, validActual, actorId, validReason, replaced?.Id);

        Audit(AuditActions.ActualCostSubmitted, actorId, now, new()
        {
            ["revisionId"] = revision.Id,
            ["actualCost"] = validActual,
            ["approvedEstimate"] = LastApprovedContent?.Amount,
            ["threshold"] = ApprovalThreshold,
            ["reason"] = validReason,
        });

        if (ApprovalRules.ActualCostNeedsApproval(validActual, ApprovalThreshold))
        {
            TransitionTo(RequestStatus.PendingApproval, AuditActions.SubmittedForApproval, actorId, now,
                RevisionDetails(revision));
        }
        else
        {
            revision.AutoApprove(now);
            Complete(validActual, now);
            TransitionTo(RequestStatus.Completed, AuditActions.AutoApproved, null, now,
                RevisionDetails(revision, triggeredBy: actorId));
        }
    }

    public void Approve(Guid approverId, Guid revisionId, string? comment, DateTimeOffset now)
    {
        EnsureAllowed(RequestAction.Approve);
        var pending = CurrentPending(revisionId);
        var validComment = Guard.OptionalText(comment, "Comment");

        pending.Approve(approverId, validComment, now);

        if (pending.Kind == RevisionKind.ActualCost)
        {
            Complete(pending.Amount, now);
            TransitionTo(RequestStatus.Completed, AuditActions.Approved, approverId, now,
                RevisionDetails(pending, comment: validComment));
        }
        else
        {
            TransitionTo(RequestStatus.Approved, AuditActions.Approved, approverId, now,
                RevisionDetails(pending, comment: validComment));
        }
    }

    public void Reject(Guid approverId, Guid revisionId, string reason, DateTimeOffset now)
    {
        EnsureAllowed(RequestAction.Reject);
        var pending = CurrentPending(revisionId);
        var validReason = Guard.Reason(reason);

        pending.Reject(approverId, validReason, now);
        var details = RevisionDetails(pending, comment: validReason);

        if (pending.Kind == RevisionKind.ActualCost)
        {
            // Work stays approved; the actual cost must be resubmitted.
            TransitionTo(RequestStatus.Approved, AuditActions.Rejected, approverId, now, details);
            return;
        }

        if (LastApprovedContent is { } fallback)
        {
            Description = fallback.Description;
            EstimatedCost = fallback.Amount;
            details["restoredRevisionId"] = fallback.Id;
            TransitionTo(RequestStatus.Approved, AuditActions.Rejected, approverId, now, details);
        }
        else
        {
            TransitionTo(RequestStatus.Rejected, AuditActions.Rejected, approverId, now, details);
        }
    }

    // --- internals ---

    private void RouteContent(RequestRevision revision, Guid actorId, DateTimeOffset now)
    {
        Description = revision.Description;
        EstimatedCost = revision.Amount;

        if (ApprovalRules.ContentNeedsApproval(revision.Amount, revision.Description, ApprovalThreshold, LastApprovedContent))
        {
            TransitionTo(RequestStatus.PendingApproval, AuditActions.SubmittedForApproval, actorId, now,
                RevisionDetails(revision));
        }
        else
        {
            revision.AutoApprove(now);
            TransitionTo(RequestStatus.Approved, AuditActions.AutoApproved, null, now,
                RevisionDetails(revision, triggeredBy: actorId));
        }
    }

    private RequestRevision? SupersedePending(Guid actorId, DateTimeOffset now)
    {
        if (PendingRevision is not { } pending) return null;
        pending.Supersede(now);
        Audit(AuditActions.RevisionSuperseded, actorId, now, new()
        {
            ["revisionId"] = pending.Id,
            ["kind"] = pending.Kind.ToString(),
        });
        return pending;
    }

    private RequestRevision CurrentPending(Guid revisionId)
    {
        var pending = PendingRevision!; // guaranteed by EnsureAllowed: Approve/Reject only exist in PendingApproval
        return pending.Id == revisionId ? pending : throw new StaleRevisionException(revisionId);
    }

    private void Complete(decimal actualCost, DateTimeOffset now)
    {
        ActualCost = actualCost;
        CompletedAt = now;
    }

    private RequestRevision AddRevision(
        RevisionKind kind, string description, decimal amount, Guid submittedById, string? reason, Guid? previousId)
    {
        var sequence = _revisions.Count == 0 ? 1 : _revisions.Max(r => r.Sequence) + 1;
        var revision = RequestRevision.Create(this, sequence, kind, description, amount, submittedById, reason, previousId);
        _revisions.Add(revision);
        return revision;
    }

    private void EnsureAllowed(RequestAction action) =>
        RequestTransitions.EnsureAllowed(Status, PendingRevision?.Kind, action);

    private void TransitionTo(
        RequestStatus next, string action, Guid? actorId, DateTimeOffset now, Dictionary<string, object?> details)
    {
        var from = Status;
        Status = next;
        // Status columns record a change only; e.g. an auto-approved edit of an Approved request stays Approved.
        if (from == next) Audit(action, actorId, now, details);
        else Audit(action, actorId, now, details, fromStatus: from, toStatus: next);
    }

    private void Audit(
        string action, Guid? actorId, DateTimeOffset now, Dictionary<string, object?> details,
        RequestStatus? fromStatus = null, RequestStatus? toStatus = null) =>
        RaiseAudit(AuditEvent.Create(OrganizationId, nameof(MaintenanceRequest), Id, action, actorId, now, details,
            fromStatus?.ToString(), toStatus?.ToString()));

    private Dictionary<string, object?> RevisionDetails(
        RequestRevision revision, Guid? triggeredBy = null, string? comment = null)
    {
        var details = new Dictionary<string, object?>
        {
            ["revisionId"] = revision.Id,
            ["kind"] = revision.Kind.ToString(),
            ["amount"] = revision.Amount,
            ["threshold"] = ApprovalThreshold,
        };
        if (triggeredBy is not null) details["triggeredBy"] = triggeredBy;
        if (comment is not null) details["comment"] = comment;
        return details;
    }
}
