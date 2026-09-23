using Assessment.Api.Domain;
using Assessment.Api.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Assessment.Api.Infrastructure.Data;

/// <summary>
/// Development-only demo data: two organizations (so tenant isolation can be tried by hand) with a few
/// requests in different states (so the list and spend report aren't empty on first run).
/// Runs only when Seed:DevelopmentData is true (appsettings.Development.json). Idempotent.
/// The password is documented in README.md and is useless outside a local database.
/// </summary>
public static class DevelopmentSeeder
{
    public const string Password = "ChangeMe-Dev-2026!";

    public static async Task SeedAsync(IServiceProvider services, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().UseSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();

        // Several app instances (or parallel test hosts) can start at once. Without serialising, each sees
        // "not seeded" and they all insert, which deadlocks or violates unique emails. The advisory lock is
        // held until the transaction ends, so later seeders wait, then see the data and stop.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(727274)", ct);

        // System scope has no tenant, so filtered reads see nothing. Check with raw SQL rather than
        // widening the IgnoreQueryFilters allow-list for a dev-only concern.
        var alreadySeeded = await db.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM users WHERE email = 'admin@acme.test'")
            .SingleAsync(ct) > 0;
        if (alreadySeeded) return;

        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();

        var acme = AddOrganization(db, hasher, "Acme Retail", "acme", ["Site 12", "Downtown Store", "Warehouse North"]);
        AddDemoRequest(db, acme, "Site 12", "Replace flickering lights in aisle 4", 800m, now, actual: 750m);
        AddDemoRequest(db, acme, "Site 12", "Repair walk-in freezer compressor", 12_000m, now, actual: 12_500m);
        AddDemoRequest(db, acme, "Downtown Store", "Replace HVAC rooftop unit", 18_000m, now);
        AddDemoRequest(db, acme, "Warehouse North", "Service loading dock doors", 3_000m, now);

        var globex = AddOrganization(db, hasher, "Globex Offices", "globex", ["HQ Tower", "Site 12", "Data Centre"]);
        AddDemoRequest(db, globex, "HQ Tower", "Fix lift door sensor", 2_400m, now, actual: 2_400m);
        AddDemoRequest(db, globex, "Site 12", "Upgrade fire suppression panel", 25_000m, now);

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private sealed record SeededOrg(Organization Org, Dictionary<string, Site> Sites, User Requester, User Approver1, User Approver2);

    private static SeededOrg AddOrganization(
        AppDbContext db, IPasswordHasher<User> hasher, string name, string emailPrefix, string[] siteNames)
    {
        var org = Organization.Create(name);
        var adminRole = Role.CreateSystemAdmin(org.Id);
        var approverRole = Role.Create(org.Id, "Approver",
            [Permissions.RequestsCreate, Permissions.RequestsManage, Permissions.RequestsApprove, Permissions.ReportsSpend]);
        var requesterRole = Role.Create(org.Id, "Requester", [Permissions.RequestsCreate]);

        db.AddRange(org, adminRole, approverRole, requesterRole);
        var sites = siteNames.ToDictionary(s => s, s => Site.Create(org.Id, s));
        db.AddRange(sites.Values);

        User NewUser(Role role, string local, string displayName) =>
            User.Create(org.Id, role.Id, $"{local}@{emailPrefix}.test", displayName, "pending");

        var users = new[]
        {
            NewUser(adminRole, "admin", $"{name} Admin"),
            NewUser(approverRole, "approver1", "Alex Approver"),
            NewUser(approverRole, "approver2", "Blake Approver"),
            NewUser(requesterRole, "requester", "Riley Requester"),
        };
        foreach (var user in users)
            user.SetPasswordHash(hasher.HashPassword(user, Password));
        db.AddRange(users);
        return new SeededOrg(org, sites, users[3], users[1], users[2]);
    }

    /// <summary>
    /// Demo requests raised by the requester through the real domain methods (so the audit trail is genuine).
    /// Over-threshold steps are approved by approver1 (estimate) and approver2 (actual); never the requester.
    /// With no actual cost the request is left where the rules put it: auto-approved, or pending approval.
    /// </summary>
    private static void AddDemoRequest(
        AppDbContext db, SeededOrg o, string site, string description, decimal estimate, DateTimeOffset now, decimal? actual = null)
    {
        var request = MaintenanceRequest.Raise(o.Org.Id, o.Sites[site].Id, o.Requester.Id, description, estimate, o.Org.ApprovalThreshold, now);
        if (actual is not null)
        {
            if (request.Status == RequestStatus.PendingApproval)
                request.Approve(o.Approver1.Id, request.PendingRevision!.Id, "Seeded approval", now);
            request.SubmitActualCost(o.Requester.Id, actual.Value, null, now);
            if (request.Status == RequestStatus.PendingApproval)
                request.Approve(o.Approver2.Id, request.PendingRevision!.Id, "Seeded approval of actual", now);
        }
        db.Add(request);
    }
}
