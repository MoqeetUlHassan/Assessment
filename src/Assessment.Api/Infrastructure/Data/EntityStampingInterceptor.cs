using Assessment.Api.Domain;
using Assessment.Api.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Assessment.Api.Infrastructure.Data;

/// <summary>
/// Sets created/updated timestamps and user stamps on every Entity, so no feature can forget them.
/// A null user id means the system (e.g. seeding) made the change.
/// </summary>
public sealed class EntityStampingInterceptor(TimeProvider clock, TenantContext tenant) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null) return;
        context.ChangeTracker.DetectChanges();
        var now = clock.GetUtcNow();

        foreach (var entry in context.ChangeTracker.Entries<Entity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAt = now;
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.CreatedById = tenant.UserId;
                    entry.Entity.UpdatedById = tenant.UserId;
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAt = now;
                    entry.Entity.UpdatedById = tenant.UserId;
                    // Creation stamps are write-once, even if something tried to change them.
                    entry.Property(e => e.CreatedAt).IsModified = false;
                    entry.Property(e => e.CreatedById).IsModified = false;
                    break;
            }
        }
    }
}
