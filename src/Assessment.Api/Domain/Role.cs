namespace Assessment.Api.Domain;

public class Role : Entity, ITenantOwned
{
    public const string OrgAdminName = "OrgAdmin";

    private List<string> _permissions = [];

    public Guid OrganizationId { get; private init; }
    public string Name { get; private set; } = null!;

    /// <summary>The OrgAdmin role: always holds every permission and cannot be edited.</summary>
    public bool IsSystemAdmin { get; private init; }

    public IReadOnlyList<string> Permissions => _permissions;

    private Role() { }

    public static Role CreateSystemAdmin(Guid organizationId) => new()
    {
        OrganizationId = organizationId,
        Name = OrgAdminName,
        IsSystemAdmin = true,
        _permissions = [.. Domain.Permissions.All.Order()],
    };

    public static Role Create(Guid organizationId, string name, IEnumerable<string> permissions)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 100)
            throw new DomainException("Role name is required and must be at most 100 characters.");
        if (string.Equals(trimmed, OrgAdminName, StringComparison.OrdinalIgnoreCase))
            throw new DomainException($"'{OrgAdminName}' is reserved.");

        return new Role { OrganizationId = organizationId, Name = trimmed, _permissions = Validate(permissions) };
    }

    /// <returns>The permissions before the change, for the audit record.</returns>
    public IReadOnlyList<string> SetPermissions(IEnumerable<string> permissions)
    {
        if (IsSystemAdmin) throw new DomainException("The OrgAdmin role cannot be edited.");
        var before = _permissions;
        _permissions = Validate(permissions);
        return before;
    }

    private static List<string> Validate(IEnumerable<string> permissions)
    {
        var set = permissions.Select(p => p.Trim()).ToHashSet();
        var unknown = set.Where(p => !Domain.Permissions.All.Contains(p)).ToList();
        if (unknown.Count > 0) throw new DomainException($"Unknown permissions: {string.Join(", ", unknown)}.");
        if (set.Overlaps(Domain.Permissions.AdminOnly))
            throw new DomainException("Admin permissions can only be held by the OrgAdmin role.");
        return [.. set.Order()];
    }
}
