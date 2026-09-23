namespace Assessment.Api.Domain;

public class User : Entity, ITenantOwned
{
    public Guid OrganizationId { get; private init; }
    public Guid RoleId { get; private set; }
    public Role Role { get; private set; } = null!;
    public string Email { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;
    public string PasswordHash { get; private set; } = null!;
    public bool IsActive { get; private set; } = true;

    private User() { }

    public static User Create(Guid organizationId, Guid roleId, string email, string displayName, string passwordHash)
    {
        var name = displayName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200)
            throw new DomainException("Display name is required and must be at most 200 characters.");

        return new User
        {
            OrganizationId = organizationId,
            RoleId = roleId,
            Email = NormalizeEmail(email),
            DisplayName = name,
            PasswordHash = passwordHash,
        };
    }

    public static string NormalizeEmail(string email)
    {
        var trimmed = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 254 || !trimmed.Contains('@'))
            throw new DomainException("A valid email is required.");
        return trimmed;
    }

    // Admin guardrails that need queries (last active OrgAdmin, not yourself) and their
    // audit events live in the admin feature (step 6).
    public void ChangeRole(Guid roleId) => RoleId = roleId;
    public void Deactivate() => IsActive = false;
    public void Reactivate() => IsActive = true;
    public void SetPasswordHash(string passwordHash) => PasswordHash = passwordHash;
}
