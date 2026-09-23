namespace Assessment.Api.Infrastructure.Tenancy;

/// <summary>
/// Request-scoped identity of the caller: their user id and the organization taken from their DB record at login.
/// The only source of the tenant for data access. Never populated from route, query or body.
///
/// Three states:
/// - unset:  reads return nothing (query filters compare against null); writes are refused.
/// - tenant: reads and writes are confined to OrganizationId.
/// - system: cross-organization writes allowed; reads still filtered. Only for seeding and test setup.
/// </summary>
public sealed class TenantContext
{
    public Guid? UserId { get; private set; }
    public Guid? OrganizationId { get; private set; }
    public bool IsSystem { get; private set; }

    public bool IsSet => OrganizationId is not null;

    public void Set(Guid organizationId, Guid userId)
    {
        if (IsSystem) throw new InvalidOperationException("A system scope cannot become a tenant scope.");
        if (IsSet && (OrganizationId != organizationId || UserId != userId))
            throw new InvalidOperationException("Tenant context is already set for this request.");
        OrganizationId = organizationId;
        UserId = userId;
    }

    /// <summary>Marks this scope as a trusted system process (seeding). Never reachable from an HTTP request.</summary>
    public void UseSystemScope()
    {
        if (IsSet) throw new InvalidOperationException("A tenant scope cannot become a system scope.");
        IsSystem = true;
    }
}
