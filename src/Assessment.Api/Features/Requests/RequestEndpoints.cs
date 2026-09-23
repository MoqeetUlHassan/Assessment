using System.Security.Claims;
using Assessment.Api.Authorization;
using Assessment.Api.Domain;
using Assessment.Api.Infrastructure.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Assessment.Api.Features.Requests;

/// <summary>
/// Every mutating endpoint follows the same order, via <see cref="MutateAsync"/>:
///   load (tenant-filtered → 404) → authorize on the loaded record (→ 403) → domain method (→ 400/409) → save (→ 409 on concurrency).
/// 404 comes first so a 403 can never confirm that another organization's request exists.
/// </summary>
public static class RequestEndpoints
{
    public static void MapRequests(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/requests").RequireAuthorization();

        group.MapGet("/", ListAsync);
        group.MapPost("/", CreateAsync).RequireAuthorization(Permissions.RequestsCreate);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapGet("/{id:guid}/history", HistoryAsync);

        group.MapPut("/{id:guid}", (Guid id, EditRequestBody body, AppDbContext db, IAuthorizationService authz,
                ClaimsPrincipal principal, CurrentUser me, TimeProvider clock, CancellationToken ct) =>
            MutateAsync(new(db, authz, principal, me, clock.GetUtcNow()), id, RequestOperations.Modify, ct,
                (r, now) => r.Edit(me.UserId, body.Description, body.EstimatedCost, body.Reason, now)));

        group.MapPost("/{id:guid}/complete", (Guid id, CompleteRequestBody body, AppDbContext db, IAuthorizationService authz,
                ClaimsPrincipal principal, CurrentUser me, TimeProvider clock, CancellationToken ct) =>
            MutateAsync(new(db, authz, principal, me, clock.GetUtcNow()), id, RequestOperations.Modify, ct,
                (r, now) => r.SubmitActualCost(me.UserId, body.ActualCost, body.Reason, now)));

        group.MapPost("/{id:guid}/approve", (Guid id, ApproveBody body, AppDbContext db, IAuthorizationService authz,
                ClaimsPrincipal principal, CurrentUser me, TimeProvider clock, CancellationToken ct) =>
            MutateAsync(new(db, authz, principal, me, clock.GetUtcNow()), id, RequestOperations.Decide, ct,
                (r, now) => r.Approve(me.UserId, body.RevisionId!.Value, body.Comment, now)))
            .RequireAuthorization(Permissions.RequestsApprove);

        group.MapPost("/{id:guid}/reject", (Guid id, RejectBody body, AppDbContext db, IAuthorizationService authz,
                ClaimsPrincipal principal, CurrentUser me, TimeProvider clock, CancellationToken ct) =>
            MutateAsync(new(db, authz, principal, me, clock.GetUtcNow()), id, RequestOperations.Decide, ct,
                (r, now) => r.Reject(me.UserId, body.RevisionId!.Value, body.Reason, now)))
            .RequireAuthorization(Permissions.RequestsApprove);
    }

    // Services are taken as plain endpoint parameters, not an [AsParameters] bundle: .NET 10's built-in validation
    // walks [AsParameters] objects and would recurse into the DbContext's object graph (500).
    private sealed record MutationContext(
        AppDbContext Db, IAuthorizationService Authz, ClaimsPrincipal Principal, CurrentUser Me, DateTimeOffset Now);

    private static async Task<IResult> MutateAsync(
        MutationContext d, Guid id, OperationAuthorizationRequirement operation, CancellationToken ct,
        Action<MaintenanceRequest, DateTimeOffset> change)
    {
        var request = await d.Db.MaintenanceRequests.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (request is null) return NotFound();

        if (!(await d.Authz.AuthorizeAsync(d.Principal, request, operation)).Succeeded)
            return Forbidden(operation);

        change(request, d.Now);          // illegal transition / stale revision / invalid input → exception handler → 409/400
        await d.Db.SaveChangesAsync(ct); // xmin conflict → 409

        return Results.Ok(await RequestReadModel.DetailAsync(d.Db, request, d.Me, ct));
    }

    private static async Task<IResult> CreateAsync(
        CreateRequestBody body, AppDbContext db, CurrentUser me, TimeProvider clock, CancellationToken ct)
    {
        // Tenant-filtered: another organization's site is indistinguishable from one that doesn't exist.
        if (!await db.Sites.AnyAsync(s => s.Id == body.SiteId, ct))
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["siteId"] = ["Site not found."] });

        var org = await db.Organizations.SingleAsync(ct); // the caller's own org (filtered)
        var request = MaintenanceRequest.Raise(
            me.OrganizationId, body.SiteId!.Value, me.UserId, body.Description, body.EstimatedCost,
            org.ApprovalThreshold, clock.GetUtcNow());

        db.Add(request);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/requests/{request.Id}", await RequestReadModel.DetailAsync(db, request, me, ct));
    }

    private static async Task<IResult> ListAsync(
        AppDbContext db, string? status, Guid? siteId, int? page, int? pageSize, CancellationToken ct)
    {
        RequestStatus? statusFilter = null;
        if (status is not null)
        {
            if (!Enum.TryParse<RequestStatus>(status, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed)
                || int.TryParse(status, out _))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                    { ["status"] = [$"Must be one of: {string.Join(", ", Enum.GetNames<RequestStatus>())}."] });
            statusFilter = parsed;
        }

        var pageNumber = Math.Max(page ?? 1, 1);
        var size = Math.Clamp(pageSize ?? 25, 1, 100);

        var requests = db.MaintenanceRequests.AsNoTracking();
        if (statusFilter is not null) requests = requests.Where(r => r.Status == statusFilter);
        if (siteId is not null) requests = requests.Where(r => r.SiteId == siteId);

        var total = await requests.CountAsync(ct);
        var items = await (
                from r in requests
                join s in db.Sites on r.SiteId equals s.Id
                join u in db.Users on r.RequestedById equals u.Id
                orderby r.CreatedAt descending, r.Id descending
                select new RequestSummary(r.Id, s.Id, s.Name, u.Id, u.DisplayName, r.Description,
                    r.EstimatedCost, r.ActualCost, r.Status.ToString(), r.CreatedAt, r.UpdatedAt))
            .Skip((pageNumber - 1) * size).Take(size)
            .ToListAsync(ct);

        return Results.Ok(new PagedResult<RequestSummary>(items, pageNumber, size, total));
    }

    private static async Task<IResult> GetAsync(Guid id, AppDbContext db, CurrentUser me, CancellationToken ct)
    {
        var request = await db.MaintenanceRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        return request is null ? NotFound() : Results.Ok(await RequestReadModel.DetailAsync(db, request, me, ct));
    }

    private static async Task<IResult> HistoryAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        var request = await db.MaintenanceRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        return request is null ? NotFound() : Results.Ok(await RequestReadModel.HistoryAsync(db, request, ct));
    }

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Request not found.");

    private static IResult Forbidden(OperationAuthorizationRequirement operation) => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: operation == RequestOperations.Decide
            ? "You can't approve or reject this request: you raised it, submitted the pending change, or lack permission."
            : "You can only change requests you raised, unless you have the requests.manage permission.");
}
