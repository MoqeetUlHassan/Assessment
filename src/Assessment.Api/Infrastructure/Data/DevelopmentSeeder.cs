using Assessment.Api.Domain;
using Assessment.Api.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Assessment.Api.Infrastructure.Data;

/// <summary>
/// Development-only demo data: two organizations so tenant isolation can be tried by hand.
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

        AddOrganization(db, hasher, "Acme Retail", "acme", ["Site 12", "Downtown Store", "Warehouse North"]);
        AddOrganization(db, hasher, "Globex Offices", "globex", ["HQ Tower", "Site 12", "Data Centre"]);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    private static void AddOrganization(
        AppDbContext db, IPasswordHasher<User> hasher, string name, string emailPrefix, string[] siteNames)
    {
        var org = Organization.Create(name);
        var adminRole = Role.CreateSystemAdmin(org.Id);
        var approverRole = Role.Create(org.Id, "Approver",
            [Permissions.RequestsCreate, Permissions.RequestsManage, Permissions.RequestsApprove, Permissions.ReportsSpend]);
        var requesterRole = Role.Create(org.Id, "Requester", [Permissions.RequestsCreate]);

        db.AddRange(org, adminRole, approverRole, requesterRole);
        db.AddRange(siteNames.Select(s => Site.Create(org.Id, s)));

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
    }
}
