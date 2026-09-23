namespace Assessment.Api.Infrastructure.Tenancy;

/// <summary>
/// Request-scoped identity of the caller: their user id and the organization taken from their DB record at login.
/// The only source of the tenant for data access. Never populated from route, query or body.
/// </summary>
public sealed class TenantContext
{
    public Guid? UserId { get; private set; }
    public Guid? OrganizationId { get; private set; }

    public bool IsSet => OrganizationId is not null;

    public void Set(Guid organizationId, Guid userId)
    {
        if (IsSet && (OrganizationId != organizationId || UserId != userId))
            throw new InvalidOperationException("Tenant context is already set for this request.");
        OrganizationId = organizationId;
        UserId = userId;
    }
}
