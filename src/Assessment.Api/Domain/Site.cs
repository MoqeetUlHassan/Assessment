namespace Assessment.Api.Domain;

public class Site : Entity, ITenantOwned
{
    public const int NameMaxLength = 200;

    public Guid OrganizationId { get; private init; }
    public string Name { get; private set; } = null!;

    private Site() { }

    /// <summary>Creates a site without an audit record (seeding, tests). Users create sites via <see cref="CreateByUser"/>.</summary>
    public static Site Create(Guid organizationId, string name) =>
        new() { OrganizationId = organizationId, Name = ValidName(name) };

    /// <summary>Any member of the organization can add a site; it always belongs to that organization.</summary>
    public static Site CreateByUser(Guid organizationId, string name, Guid actorId, DateTimeOffset now)
    {
        var site = Create(organizationId, name);
        site.RaiseAudit(AuditEvent.Create(organizationId, nameof(Site), site.Id, AuditActions.SiteCreated, actorId, now,
            new Dictionary<string, object?> { ["name"] = site.Name }));
        return site;
    }

    private static string ValidName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > NameMaxLength)
            throw new DomainException($"Site name is required and must be at most {NameMaxLength} characters.");
        return trimmed;
    }
}
