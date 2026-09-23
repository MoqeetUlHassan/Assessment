using Assessment.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Assessment.Api.Tests.Persistence;

// Guarantees the database itself must hold, even if application code is wrong or bypassed.
// These are the claims we will be asked to prove: audit immutability, atomic audit writes,
// cross-tenant references rejected, and the one-pending-revision invariant.
public class SchemaGuaranteesTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Theory]
    [InlineData("UPDATE audit_events SET action = 'Tampered' WHERE organization_id = {0}")]
    [InlineData("DELETE FROM audit_events WHERE organization_id = {0}")]
    [InlineData("TRUNCATE audit_events")]
    public async Task Audit_trail_cannot_be_modified_even_with_raw_sql(string sql)
    {
        var tenant = await TestTenant.CreateAsync(factory);
        await tenant.SaveNewRequestAsync(factory, 5_000m, Now);
        using var scope = tenant.AsRequester(factory);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => scope.Db.Database.ExecuteSqlRawAsync(sql, tenant.Org.Id));

        Assert.Contains("append-only", ex.MessageText);
        Assert.Equal(2, await scope.Db.AuditEvents.CountAsync());
    }

    [Fact]
    public async Task State_change_and_its_audit_events_commit_together()
    {
        var tenant = await TestTenant.CreateAsync(factory);
        var request = await tenant.SaveNewRequestAsync(factory, 15_000m, Now);
        using var scope = tenant.AsRequester(factory);

        var actions = await scope.Db.AuditEvents
            .Where(a => a.EntityId == request.Id).OrderBy(a => a.Id).Select(a => a.Action).ToListAsync();
        Assert.Equal([AuditActions.Raised, AuditActions.SubmittedForApproval], actions);
    }

    [Fact]
    public async Task Request_against_another_organizations_site_is_rejected_and_leaves_no_audit_trace()
    {
        var mine = await TestTenant.CreateAsync(factory);
        var theirs = await TestTenant.CreateAsync(factory);
        using var scope = mine.AsRequester(factory);

        // Simulates an application bug that let a foreign site id through. The request's own org is correct,
        // so the app-level tenant guard passes; the composite FK is the layer that must catch this.
        var request = MaintenanceRequest.Raise(mine.Org.Id, theirs.Site.Id, mine.Requester.Id, "Fix", 5_000m, 10_000m, Now);
        scope.Db.Add(request);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => scope.Db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ((PostgresException)ex.InnerException!).SqlState);

        scope.Db.ChangeTracker.Clear();
        Assert.False(await scope.Db.MaintenanceRequests.AnyAsync(r => r.Id == request.Id));
        Assert.False(await scope.Db.AuditEvents.AnyAsync(a => a.EntityId == request.Id));
    }

    [Fact]
    public async Task Database_allows_at_most_one_pending_revision_per_request()
    {
        var tenant = await TestTenant.CreateAsync(factory);
        var request = await tenant.SaveNewRequestAsync(factory, 15_000m, Now); // has one pending revision
        using var scope = tenant.AsRequester(factory);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => scope.Db.Database.ExecuteSqlRawAsync("""
            INSERT INTO request_revisions (id, organization_id, request_id, sequence, kind, description, amount,
                submitted_by_id, outcome, created_at, updated_at)
            VALUES (gen_random_uuid(), {0}, {1}, 99, 'Edit', 'sneaky', 1, {2}, 'Pending', now(), now())
            """, tenant.Org.Id, request.Id, tenant.Requester.Id));

        Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
    }

    [Fact]
    public async Task Database_rejects_a_revision_decided_by_its_own_submitter()
    {
        var tenant = await TestTenant.CreateAsync(factory);
        var saved = await tenant.SaveNewRequestAsync(factory, 15_000m, Now);
        using var scope = tenant.AsRequester(factory);
        var request = await scope.Db.MaintenanceRequests.SingleAsync(r => r.Id == saved.Id);

        // Authorization should stop this first; the CHECK is the backstop if it ever doesn't.
        request.Approve(tenant.Requester.Id, request.PendingRevision!.Id, null, Now);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => scope.Db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, ((PostgresException)ex.InnerException!).SqlState);
    }

    [Fact]
    public async Task Created_stamps_are_write_once_and_updated_stamps_name_the_acting_user()
    {
        var tenant = await TestTenant.CreateAsync(factory);
        var saved = await tenant.SaveNewRequestAsync(factory, 15_000m, Now);

        using var scope = tenant.AsApprover(factory);
        var loaded = await scope.Db.MaintenanceRequests.SingleAsync(r => r.Id == saved.Id);
        var createdAt = loaded.CreatedAt;
        loaded.Approve(tenant.Approver.Id, loaded.PendingRevision!.Id, "ok", Now); // revisions came via auto-include
        await scope.Db.SaveChangesAsync();

        scope.Db.ChangeTracker.Clear();
        var reloaded = await scope.Db.MaintenanceRequests.SingleAsync(r => r.Id == saved.Id);
        Assert.Equal(RequestStatus.Approved, reloaded.Status);
        Assert.Equal(createdAt, reloaded.CreatedAt);
        Assert.Equal(tenant.Requester.Id, reloaded.CreatedById);
        Assert.Equal(tenant.Approver.Id, reloaded.UpdatedById);
        Assert.True(reloaded.UpdatedAt > reloaded.CreatedAt);
    }
}
