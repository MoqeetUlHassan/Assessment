namespace Assessment.Api.Domain;

/// <summary>
/// Append-only compliance record. No update or delete path exists in the app,
/// and a database trigger rejects UPDATE/DELETE/TRUNCATE on the table.
/// One event = one action; FromStatus/ToStatus are set only when the action changed the request's status.
/// </summary>
public class AuditEvent : ITenantOwned
{
    public long Id { get; private set; }
    public Guid OrganizationId { get; private init; }
    public string EntityType { get; private init; } = null!;
    public Guid EntityId { get; private init; }
    public string Action { get; private init; } = null!;
    public string? FromStatus { get; private init; }
    public string? ToStatus { get; private init; }

    /// <summary>Null means the system acted (e.g. auto-approval); details then name the triggering user.</summary>
    public Guid? ActorUserId { get; private init; }

    public IReadOnlyDictionary<string, object?> Details { get; private init; } = new Dictionary<string, object?>();
    public DateTimeOffset CreatedAt { get; private init; }

    private AuditEvent() { }

    public static AuditEvent Create(
        Guid organizationId, string entityType, Guid entityId, string action, Guid? actorUserId,
        DateTimeOffset now, IReadOnlyDictionary<string, object?>? details = null,
        string? fromStatus = null, string? toStatus = null) => new()
    {
        OrganizationId = organizationId,
        EntityType = entityType,
        EntityId = entityId,
        Action = action,
        ActorUserId = actorUserId,
        FromStatus = fromStatus,
        ToStatus = toStatus,
        Details = details ?? new Dictionary<string, object?>(),
        CreatedAt = now,
    };
}

public static class AuditActions
{
    public const string Raised = "Raised";
    public const string AutoApproved = "AutoApproved";
    public const string SubmittedForApproval = "SubmittedForApproval";
    public const string Edited = "Edited";
    public const string RevisionSuperseded = "RevisionSuperseded";
    public const string ActualCostSubmitted = "ActualCostSubmitted";
    public const string Approved = "Approved";
    public const string Rejected = "Rejected";
    public const string ThresholdChanged = "ThresholdChanged";
    public const string UserCreated = "UserCreated";
    public const string UserRoleChanged = "UserRoleChanged";
    public const string UserDeactivated = "UserDeactivated";
    public const string UserReactivated = "UserReactivated";
    public const string PasswordReset = "PasswordReset";
    public const string RoleCreated = "RoleCreated";
    public const string RolePermissionsChanged = "RolePermissionsChanged";
}
