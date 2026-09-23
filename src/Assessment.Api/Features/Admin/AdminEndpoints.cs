using Assessment.Api.Authorization;
using Assessment.Api.Domain;
using Assessment.Api.Features.Auth;
using Assessment.Api.Features.Requests;
using Assessment.Api.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Assessment.Api.Features.Admin;

/// <summary>
/// OrgAdmin endpoints. Everything is tenant-filtered: another organization's user or role id is a 404,
/// and anything created lands in the caller's organization (there is no organization field to send).
/// Each action raises its own audit event (actor, before/after) from the domain.
/// </summary>
public static class AdminEndpoints
{
    private static readonly Dictionary<string, string> PermissionDescriptions = new()
    {
        [Permissions.RequestsCreate] = "Raise requests; edit and complete own requests",
        [Permissions.RequestsManage] = "Edit and complete any request in the organization",
        [Permissions.RequestsApprove] = "Approve or reject pending changes (never own requests or own changes)",
        [Permissions.ReportsSpend] = "View the spend report",
        [Permissions.AdminUsers] = "Create users, change roles, deactivate, reset passwords",
        [Permissions.AdminRoles] = "Create roles and edit non-admin role permissions",
        [Permissions.AdminSettings] = "Change the approval threshold",
    };

    public static void MapAdmin(this IEndpointRouteBuilder app)
    {
        var users = app.MapGroup("/api/admin/users").RequireAuthorization(Permissions.AdminUsers);
        users.MapGet("/", ListUsersAsync);
        users.MapPost("/", CreateUserAsync);
        users.MapPut("/{id:guid}/role", (Guid id, ChangeRoleBody body, AppDbContext db, CurrentUser me, TimeProvider clock, CancellationToken ct) =>
            WithUserAsync(db, id, ct, async user =>
            {
                var role = await db.Roles.SingleOrDefaultAsync(r => r.Id == body.RoleId, ct);
                if (role is null) return RoleNotFound();
                user.ChangeRole(role, me.UserId, clock.GetUtcNow());
                return null;
            }));
        users.MapPost("/{id:guid}/deactivate", (Guid id, AppDbContext db, CurrentUser me, TimeProvider clock, CancellationToken ct) =>
            WithUserAsync(db, id, ct, user => { user.Deactivate(me.UserId, clock.GetUtcNow()); return Task.FromResult<IResult?>(null); }));
        users.MapPost("/{id:guid}/reactivate", (Guid id, AppDbContext db, CurrentUser me, TimeProvider clock, CancellationToken ct) =>
            WithUserAsync(db, id, ct, user => { user.Reactivate(me.UserId, clock.GetUtcNow()); return Task.FromResult<IResult?>(null); }));
        users.MapPost("/{id:guid}/password", (Guid id, ResetPasswordBody body, AppDbContext db, IPasswordHasher<User> hasher,
                CurrentUser me, TimeProvider clock, CancellationToken ct) =>
            WithUserAsync(db, id, ct, user =>
            {
                User.EnsurePasswordPolicy(body.Password);
                user.ResetPassword(hasher.HashPassword(user, body.Password), me.UserId, clock.GetUtcNow());
                return Task.FromResult<IResult?>(null);
            }));

        var roles = app.MapGroup("/api/admin/roles").RequireAuthorization(Permissions.AdminRoles);
        roles.MapGet("/", ListRolesAsync);
        roles.MapPost("/", CreateRoleAsync);
        roles.MapPut("/{id:guid}/permissions", ChangePermissionsAsync);

        app.MapGet("/api/admin/permissions", () => Results.Ok(Permissions.All.Order()
                .Select(p => new PermissionView(p, PermissionDescriptions[p], Permissions.AdminOnly.Contains(p)))))
            .RequireAuthorization(Permissions.AdminRoles);

        app.MapPut("/api/admin/settings/threshold", ChangeThresholdAsync).RequireAuthorization(Permissions.AdminSettings);
        app.MapGet("/api/admin/audit", ListAuditAsync).RequireAuthorization(Permissions.AdminUsers);
    }

    // --- Users ---

    private static async Task<IResult> ListUsersAsync(AppDbContext db, CancellationToken ct)
    {
        var users = await db.Users.AsNoTracking().Include(u => u.Role).OrderBy(u => u.DisplayName).ToListAsync(ct);
        var names = users.ToDictionary(u => u.Id, u => u.DisplayName);
        return Results.Ok(users.Select(u => ToView(u, names)));
    }

    private static async Task<IResult> CreateUserAsync(
        CreateUserBody body, AppDbContext db, IPasswordHasher<User> hasher, CurrentUser me, TimeProvider clock, CancellationToken ct)
    {
        var role = await db.Roles.SingleOrDefaultAsync(r => r.Id == body.RoleId, ct); // own org only
        if (role is null) return RoleNotFound();

        User.EnsurePasswordPolicy(body.Password);
        var user = User.CreateByAdmin(me.OrganizationId, role, body.Email, body.DisplayName,
            u => hasher.HashPassword(u, body.Password), me.UserId, clock.GetUtcNow());
        db.Add(user);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ix_users_email" })
        {
            // Emails are globally unique (login needs no org code). Accepted trade-off: this reveals the email
            // is in use somewhere. See DECISIONS.md.
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "That email is already in use.");
        }

        return Results.Created($"/api/admin/users/{user.Id}", await UserViewAsync(db, user.Id, ct));
    }

    private static async Task<IResult> WithUserAsync(
        AppDbContext db, Guid id, CancellationToken ct, Func<User, Task<IResult?>> change)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "User not found.");

        var early = await change(user); // guardrail violations throw DomainException → 400
        if (early is not null) return early;

        await db.SaveChangesAsync(ct);
        return Results.Ok(await UserViewAsync(db, user.Id, ct));
    }

    private static async Task<UserView> UserViewAsync(AppDbContext db, Guid id, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().Include(u => u.Role).SingleAsync(u => u.Id == id, ct);
        var creator = user.CreatedById is { } c
            ? await db.Users.Where(u => u.Id == c).Select(u => u.DisplayName).SingleOrDefaultAsync(ct)
            : null;
        return ToView(user, creator is null ? new Dictionary<Guid, string>() : new() { [user.CreatedById!.Value] = creator });
    }

    private static UserView ToView(User u, IReadOnlyDictionary<Guid, string> names) => new(
        u.Id, u.DisplayName, u.Email, u.RoleId, u.Role.Name, u.IsActive, u.CreatedAt,
        u.CreatedById is { } c ? names.GetValueOrDefault(c) : "System");

    // --- Roles ---

    private static async Task<IResult> ListRolesAsync(AppDbContext db, CancellationToken ct)
    {
        var roles = await db.Roles.AsNoTracking().OrderByDescending(r => r.IsSystemAdmin).ThenBy(r => r.Name).ToListAsync(ct);
        var counts = await db.Users.GroupBy(u => u.RoleId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return Results.Ok(roles.Select(r => new RoleView(r.Id, r.Name, r.IsSystemAdmin, r.Permissions, counts.GetValueOrDefault(r.Id))));
    }

    private static async Task<IResult> CreateRoleAsync(
        CreateRoleBody body, AppDbContext db, CurrentUser me, TimeProvider clock, CancellationToken ct)
    {
        if (await db.Roles.AnyAsync(r => r.Name == body.Name.Trim(), ct))
            return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "A role with that name already exists.");

        var role = Role.CreateByAdmin(me.OrganizationId, body.Name, body.Permissions, me.UserId, clock.GetUtcNow());
        db.Add(role);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/admin/roles/{role.Id}", new RoleView(role.Id, role.Name, false, role.Permissions, 0));
    }

    private static async Task<IResult> ChangePermissionsAsync(
        Guid id, ChangePermissionsBody body, AppDbContext db, CurrentUser me, TimeProvider clock, CancellationToken ct)
    {
        var role = await db.Roles.SingleOrDefaultAsync(r => r.Id == id, ct);
        if (role is null) return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Role not found.");

        role.ChangePermissions(body.Permissions, me.UserId, clock.GetUtcNow()); // admin-only / OrgAdmin guards → 400
        await db.SaveChangesAsync(ct);
        var userCount = await db.Users.CountAsync(u => u.RoleId == id, ct);
        return Results.Ok(new RoleView(role.Id, role.Name, role.IsSystemAdmin, role.Permissions, userCount));
    }

    // --- Settings & audit ---

    private static async Task<IResult> ChangeThresholdAsync(
        ChangeThresholdBody body, AppDbContext db, CurrentUser me, TimeProvider clock, CancellationToken ct)
    {
        var org = await db.Organizations.SingleAsync(ct); // the caller's own org (filtered)
        org.ChangeApprovalThreshold(body.Amount, me.UserId, body.Reason, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return Results.Ok(new OrganizationSummary(org.Id, org.Name, org.ApprovalThreshold));
    }

    /// <summary>The organization-wide audit log: requests, users, roles and threshold changes, newest first.</summary>
    private static async Task<IResult> ListAuditAsync(AppDbContext db, string? entityType, int? page, int? pageSize, CancellationToken ct)
    {
        var pageNumber = Math.Max(page ?? 1, 1);
        var size = Math.Clamp(pageSize ?? 50, 1, 200);

        var query = db.AuditEvents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(entityType)) query = query.Where(a => a.EntityType == entityType);

        var total = await query.CountAsync(ct);
        var events = await query.OrderByDescending(a => a.Id).Skip((pageNumber - 1) * size).Take(size).ToListAsync(ct);
        var actorIds = events.Where(e => e.ActorUserId is not null).Select(e => e.ActorUserId!.Value).Distinct().ToList();
        var names = await db.Users.Where(u => actorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);

        var items = events.Select(e => new OrgAuditEntry(e.Id, e.EntityType, e.EntityId, e.Action, e.FromStatus, e.ToStatus,
            e.ActorUserId is { } a ? names.GetValueOrDefault(a, "?") : "System", e.Details, e.CreatedAt)).ToList();
        return Results.Ok(new PagedResult<OrgAuditEntry>(items, pageNumber, size, total));
    }

    private static IResult RoleNotFound() =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["roleId"] = ["Role not found."] });
}
