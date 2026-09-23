using Assessment.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Assessment.Api.Infrastructure.Data;

/// <summary>
/// Moves audit events raised by entities into the audit_events table as part of the SAME SaveChanges,
/// so a state change and its audit record commit or roll back together.
/// </summary>
public sealed class AuditEventInterceptor : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private static void Collect(DbContext? context)
    {
        if (context is null) return;
        context.ChangeTracker.DetectChanges();

        foreach (var entry in context.ChangeTracker.Entries<Entity>().ToList())
        {
            if (entry.Entity.PendingAuditEvents.Count == 0) continue;
            context.Set<AuditEvent>().AddRange(entry.Entity.PendingAuditEvents);
            entry.Entity.ClearPendingAuditEvents();
        }
    }
}
