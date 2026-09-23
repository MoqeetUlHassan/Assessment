using Assessment.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Assessment.Api.Infrastructure.Tenancy;

/// <summary>A write crossed the tenant boundary. Always a bug, never user error: surfaces as 500 and is logged.</summary>
public sealed class TenantIsolationException(string message) : InvalidOperationException(message);

/// <summary>
/// Isolation layer 3: every row being inserted, updated or deleted must belong to the caller's organization.
/// Verifies rather than stamps: a wrong OrganizationId is a bug to surface, not something to silently "fix".
/// Runs after audit collection so audit rows are checked too.
/// </summary>
public sealed class TenantGuardInterceptor(TenantContext tenant) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Check(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Check(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Check(DbContext? context)
    {
        if (context is null) return;
        context.ChangeTracker.DetectChanges();

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;

            // Append-only in the app too, not just via the DB trigger. Applies even to system scope.
            if (entry.Entity is AuditEvent && entry.State != EntityState.Added)
                throw new TenantIsolationException("Audit events are append-only.");

            if (tenant.IsSystem) continue; // seeding: cross-organization writes allowed

            if (!tenant.IsSet)
                throw new TenantIsolationException(
                    $"Refusing to save {entry.Metadata.ClrType.Name}: no tenant in scope.");

            var owner = entry.Entity switch
            {
                ITenantOwned owned => owned.OrganizationId,
                Organization org => org.Id,
                _ => throw new TenantIsolationException(
                    $"{entry.Metadata.ClrType.Name} is not tenant-scoped and cannot be written by a tenant."),
            };

            if (entry.Entity is Organization && entry.State == EntityState.Added)
                throw new TenantIsolationException("Organizations can only be created by the system.");

            if (owner != tenant.OrganizationId)
                throw new TenantIsolationException(
                    $"Refusing to {entry.State} {entry.Metadata.ClrType.Name} of another organization.");
        }
    }
}
