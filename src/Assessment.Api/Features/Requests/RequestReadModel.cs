using Assessment.Api.Authorization;
using Assessment.Api.Domain;
using Assessment.Api.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Assessment.Api.Features.Requests;

/// <summary>Builds response shapes. All queries are tenant-filtered; names are resolved within the caller's org only.</summary>
internal static class RequestReadModel
{
    public static async Task<RequestDetail> DetailAsync(
        AppDbContext db, MaintenanceRequest request, CurrentUser me, CancellationToken ct)
    {
        var siteName = await db.Sites.Where(s => s.Id == request.SiteId).Select(s => s.Name).SingleAsync(ct);
        var names = await UserNamesAsync(db, request.Revisions
            .SelectMany(r => new[] { r.SubmittedById, r.DecidedById ?? Guid.Empty })
            .Append(request.RequestedById), ct);

        var revisions = request.Revisions.OrderBy(r => r.Sequence).Select(r => ToView(r, names)).ToList();
        var allowed = request.AllowedActions;
        var cannotModify = MaintenanceRequestAuthorizationHandler.WhyCannotModify(me, request);
        var cannotDecide = MaintenanceRequestAuthorizationHandler.WhyCannotDecide(me, request);
        var canModify = cannotModify is null;
        var canDecide = cannotDecide is null;
        var stateAllowsDecision = allowed.Contains(RequestAction.Approve) || allowed.Contains(RequestAction.Reject);
        var stateAllowsChange = allowed.Contains(RequestAction.Edit) || allowed.Contains(RequestAction.SubmitActualCost);

        return new RequestDetail(
            request.Id, request.SiteId, siteName, request.RequestedById, names.GetValueOrDefault(request.RequestedById, "?"),
            request.Description, request.EstimatedCost, request.ActualCost, request.ApprovalThreshold,
            request.Status.ToString(),
            revisions.SingleOrDefault(r => r.Outcome == nameof(RevisionOutcome.Pending)),
            revisions,
            new AvailableActions(
                CanEdit: canModify && allowed.Contains(RequestAction.Edit),
                CanSubmitActualCost: canModify && allowed.Contains(RequestAction.SubmitActualCost),
                CanApprove: canDecide && allowed.Contains(RequestAction.Approve),
                CanReject: canDecide && allowed.Contains(RequestAction.Reject),
                CannotDecideReason: stateAllowsDecision ? cannotDecide : null,
                CannotModifyReason: stateAllowsChange ? cannotModify : null),
            request.CompletedAt, request.CreatedAt, request.UpdatedAt);
    }

    public static async Task<RequestHistory> HistoryAsync(AppDbContext db, MaintenanceRequest request, CancellationToken ct)
    {
        var events = await db.AuditEvents.AsNoTracking()
            .Where(a => a.EntityType == nameof(MaintenanceRequest) && a.EntityId == request.Id)
            .OrderBy(a => a.Id)
            .ToListAsync(ct);

        var names = await UserNamesAsync(db, request.Revisions
            .SelectMany(r => new[] { r.SubmittedById, r.DecidedById ?? Guid.Empty })
            .Concat(events.Select(e => e.ActorUserId ?? Guid.Empty)), ct);

        return new RequestHistory(
            request.Id,
            request.Revisions.OrderBy(r => r.Sequence).Select(r => ToView(r, names)).ToList(),
            events.Select(e => new AuditEntryView(
                e.Id, e.Action, e.FromStatus, e.ToStatus, e.ActorUserId,
                e.ActorUserId is { } actor ? names.GetValueOrDefault(actor, "?") : "System",
                e.Details, e.CreatedAt)).ToList());
    }

    private static RevisionView ToView(RequestRevision r, IReadOnlyDictionary<Guid, string> names) => new(
        r.Id, r.Sequence, r.Kind.ToString(), r.Description, r.Amount, r.Reason,
        r.SubmittedById, names.GetValueOrDefault(r.SubmittedById, "?"), r.Outcome.ToString(),
        r.DecidedById, r.DecidedById is { } d ? names.GetValueOrDefault(d, "?")
            : r.Outcome == RevisionOutcome.AutoApproved ? "System" : null,
        r.DecidedAt, r.DecisionComment, r.CreatedAt);

    private static async Task<IReadOnlyDictionary<Guid, string>> UserNamesAsync(
        AppDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Where(id => id != Guid.Empty).Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => wanted.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }
}
