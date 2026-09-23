using Assessment.Api.Domain;
using Assessment.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Assessment.Api.Tests.Persistence;

// Isolation layers 2 (reads) and 3 (writes), tested the way an attacker would try: with real ids from
// another organization. HTTP-level cross-tenant tests come with the endpoints (step 5); these prove the
// data layer holds even if an endpoint forgets to check anything.
public class TenantIsolationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task Another_organizations_rows_do_not_exist_for_you_even_by_exact_id()
    {
        var victim = await TestTenant.CreateAsync(factory);
        var request = await victim.SaveNewRequestAsync(factory, 15_000m, Now);
        var attacker = await TestTenant.CreateAsync(factory);

        using var scope = attacker.AsApprover(factory);
        var db = scope.Db;

        Assert.Null(await db.MaintenanceRequests.FindAsync(request.Id));
        Assert.Null(await db.MaintenanceRequests.FirstOrDefaultAsync(r => r.Id == request.Id));
        Assert.False(await db.RequestRevisions.AnyAsync(r => r.RequestId == request.Id));
        Assert.False(await db.AuditEvents.AnyAsync(a => a.EntityId == request.Id));
        Assert.Null(await db.Sites.FindAsync(victim.Site.Id));
        Assert.Null(await db.Users.FindAsync(victim.Approver.Id));
        Assert.Null(await db.Roles.FirstOrDefaultAsync(r => r.OrganizationId == victim.Org.Id));
        Assert.Null(await db.Organizations.FindAsync(victim.Org.Id));

        // Positive control: the same scope does see its own data, so the nulls above mean isolation, not breakage.
        Assert.NotNull(await db.Sites.FindAsync(attacker.Site.Id));
        Assert.NotNull(await db.Organizations.FindAsync(attacker.Org.Id));
    }

    [Fact]
    public async Task Without_a_tenant_every_query_returns_nothing()
    {
        var tenant = await TestTenant.CreateAsync(factory);
        await tenant.SaveNewRequestAsync(factory, 5_000m, Now);

        using var scope = DbScope.Anonymous(factory);

        // Fail closed: a code path that forgot to establish a tenant sees no data, rather than all of it.
        Assert.Empty(await scope.Db.MaintenanceRequests.ToListAsync());
        Assert.Empty(await scope.Db.Users.ToListAsync());
        Assert.Empty(await scope.Db.Organizations.ToListAsync());
        Assert.Empty(await scope.Db.AuditEvents.ToListAsync());
    }

    [Fact]
    public async Task Modifying_another_organizations_entity_is_refused_even_if_attached_directly()
    {
        var victim = await TestTenant.CreateAsync(factory);
        var request = await victim.SaveNewRequestAsync(factory, 15_000m, Now);
        var attacker = await TestTenant.CreateAsync(factory);

        // Load the victim's request in a legitimate scope, then smuggle the instance into the attacker's context.
        MaintenanceRequest smuggled;
        using (var victimScope = victim.AsApprover(factory))
            smuggled = await victimScope.Db.MaintenanceRequests.SingleAsync(r => r.Id == request.Id);

        using var scope = attacker.AsApprover(factory);
        scope.Db.Attach(smuggled);
        smuggled.Approve(attacker.Approver.Id, smuggled.PendingRevision!.Id, "mine now", Now);

        var ex = await Assert.ThrowsAsync<TenantIsolationException>(() => scope.Db.SaveChangesAsync());
        Assert.Contains("another organization", ex.Message);

        using var check = victim.AsApprover(factory);
        Assert.Equal(RequestStatus.PendingApproval,
            (await check.Db.MaintenanceRequests.SingleAsync(r => r.Id == request.Id)).Status);
    }

    [Fact]
    public async Task Creating_rows_for_another_organization_is_refused()
    {
        var victim = await TestTenant.CreateAsync(factory);
        var attacker = await TestTenant.CreateAsync(factory);

        using var scope = attacker.AsApprover(factory);
        scope.Db.Add(Site.Create(victim.Org.Id, "Planted site"));

        await Assert.ThrowsAsync<TenantIsolationException>(() => scope.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task Writes_without_a_tenant_are_refused()
    {
        var tenant = await TestTenant.CreateAsync(factory);
        using var scope = DbScope.Anonymous(factory);
        scope.Db.Add(Site.Create(tenant.Org.Id, "Orphan"));

        await Assert.ThrowsAsync<TenantIsolationException>(() => scope.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task Tenants_cannot_create_organizations()
    {
        var tenant = await TestTenant.CreateAsync(factory);
        using var scope = tenant.AsApprover(factory);
        scope.Db.Add(Organization.Create("Shadow org"));

        await Assert.ThrowsAsync<TenantIsolationException>(() => scope.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task Audit_events_cannot_be_modified_through_the_app_even_by_the_system()
    {
        var tenant = await TestTenant.CreateAsync(factory);
        await tenant.SaveNewRequestAsync(factory, 5_000m, Now);

        using var scope = DbScope.System(factory);
        // System scope reads are still filtered (no tenant), so fetch the row without filters for this test only.
        var audit = await scope.Db.AuditEvents.IgnoreQueryFilters().FirstAsync(a => a.OrganizationId == tenant.Org.Id);
        scope.Db.Remove(audit);

        var ex = await Assert.ThrowsAsync<TenantIsolationException>(() => scope.Db.SaveChangesAsync());
        Assert.Contains("append-only", ex.Message);
    }
}
