namespace Assessment.Api.Domain;

/// <summary>Rows owned by one organization. Every such entity gets a global query filter.</summary>
public interface ITenantOwned
{
    Guid OrganizationId { get; }
}

public abstract class Entity
{
    private readonly List<AuditEvent> _auditEvents = [];

    public Guid Id { get; protected init; } = Guid.CreateVersion7();

    // Set by the SaveChanges interceptor only; never by feature code.
    public DateTimeOffset CreatedAt { get; internal set; }
    public DateTimeOffset UpdatedAt { get; internal set; }
    public Guid? CreatedById { get; internal set; }
    public Guid? UpdatedById { get; internal set; }

    /// <summary>Audit events raised by this entity, persisted in the same SaveChanges as the change itself.</summary>
    public IReadOnlyList<AuditEvent> PendingAuditEvents => _auditEvents;

    internal void ClearPendingAuditEvents() => _auditEvents.Clear();

    protected void RaiseAudit(AuditEvent auditEvent) => _auditEvents.Add(auditEvent);
}
