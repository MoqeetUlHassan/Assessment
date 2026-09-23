using System.ComponentModel.DataAnnotations;

namespace Assessment.Api.Features.Requests;

// Boundary validation (shape and ranges). Business invariants (2 decimal places, state legality)
// are re-checked by the domain, which is the authority.

public sealed record CreateRequestBody(
    [property: Required] Guid? SiteId,
    [property: Required, MaxLength(2000)] string Description,
    [property: Range(0.01, 10_000_000)] decimal EstimatedCost);

public sealed record EditRequestBody(
    [property: Required, MaxLength(2000)] string Description,
    [property: Range(0.01, 10_000_000)] decimal EstimatedCost,
    [property: Required, MaxLength(1000)] string Reason);

public sealed record CompleteRequestBody(
    [property: Range(0.01, 10_000_000)] decimal ActualCost,
    [property: MaxLength(1000)] string? Reason);

public sealed record ApproveBody(
    [property: Required] Guid? RevisionId,
    [property: MaxLength(1000)] string? Comment);

public sealed record RejectBody(
    [property: Required] Guid? RevisionId,
    [property: Required, MaxLength(1000)] string Reason);

public sealed record RequestSummary(
    Guid Id, Guid SiteId, string SiteName, Guid RequestedById, string RequestedByName, string Description,
    decimal EstimatedCost, decimal? ActualCost, string Status, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record RevisionView(
    Guid Id, int Sequence, string Kind, string Description, decimal Amount, string? Reason,
    Guid SubmittedById, string SubmittedByName, string Outcome,
    Guid? DecidedById, string? DecidedByName, DateTimeOffset? DecidedAt, string? DecisionComment,
    DateTimeOffset CreatedAt);

/// <summary>
/// What the caller may do right now: the transition table intersected with their permissions.
/// A convenience for the UI only; every action is enforced again server-side.
/// </summary>
public sealed record AvailableActions(bool CanEdit, bool CanSubmitActualCost, bool CanApprove, bool CanReject);

public sealed record RequestDetail(
    Guid Id, Guid SiteId, string SiteName, Guid RequestedById, string RequestedByName,
    string Description, decimal EstimatedCost, decimal? ActualCost, decimal ApprovalThreshold,
    string Status, RevisionView? PendingRevision, IReadOnlyList<RevisionView> Revisions,
    AvailableActions Actions, DateTimeOffset? CompletedAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record AuditEntryView(
    long Id, string Action, string? FromStatus, string? ToStatus, Guid? ActorUserId, string ActorName,
    IReadOnlyDictionary<string, object?> Details, DateTimeOffset At);

public sealed record RequestHistory(Guid RequestId, IReadOnlyList<RevisionView> Revisions, IReadOnlyList<AuditEntryView> Events);
