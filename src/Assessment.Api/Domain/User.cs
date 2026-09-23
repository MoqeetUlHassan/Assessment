namespace Assessment.Api.Domain;

public class User : Entity, ITenantOwned
{
    public const int MinPasswordLength = 12;

    public Guid OrganizationId { get; private init; }
    public Guid RoleId { get; private set; }
    public Role Role { get; private set; } = null!;
    public string Email { get; private set; } = null!;
    public string DisplayName { get; private set; } = null!;
    public string PasswordHash { get; private set; } = null!;
    public bool IsActive { get; private set; } = true;

    private User() { }

    /// <summary>Creates a user without an audit record (seeding, tests). Admin creation uses <see cref="CreateByAdmin"/>.</summary>
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

    public static User CreateByAdmin(
        Guid organizationId, Role role, string email, string displayName, Func<User, string> hashPassword,
        Guid actorId, DateTimeOffset now)
    {
        var user = Create(organizationId, role.Id, email, displayName, "pending");
        user.PasswordHash = hashPassword(user);
        user.Audit(AuditActions.UserCreated, actorId, now, new()
        {
            ["email"] = user.Email, ["displayName"] = user.DisplayName, ["roleId"] = role.Id, ["role"] = role.Name,
        });
        return user;
    }

    public static string NormalizeEmail(string email)
    {
        var trimmed = email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 254 || !trimmed.Contains('@'))
            throw new DomainException("A valid email is required.");
        return trimmed;
    }

    public static void EnsurePasswordPolicy(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength || password.Length > 200)
            throw new DomainException($"Password must be between {MinPasswordLength} and 200 characters.");
    }

    // Guardrails that depend on WHO is acting (not yourself) are checked here, with the actor passed in.
    // A "last active OrgAdmin" check is deliberately absent: only OrgAdmins hold admin.users (locked to
    // that role) and they cannot deactivate or demote themselves, so the acting admin always remains.

    public void ChangeRole(Role newRole, Guid actorId, DateTimeOffset now)
    {
        if (actorId == Id) throw new DomainException("You can't change your own role.");
        if (newRole.Id == RoleId) throw new DomainException("The user already has this role.");
        var from = RoleId;
        RoleId = newRole.Id;
        Audit(AuditActions.UserRoleChanged, actorId, now, new()
            { ["fromRoleId"] = from, ["toRoleId"] = newRole.Id, ["toRole"] = newRole.Name });
    }

    public void Deactivate(Guid actorId, DateTimeOffset now)
    {
        if (actorId == Id) throw new DomainException("You can't deactivate yourself.");
        if (!IsActive) throw new DomainException("The user is already inactive.");
        IsActive = false;
        Audit(AuditActions.UserDeactivated, actorId, now, new());
    }

    public void Reactivate(Guid actorId, DateTimeOffset now)
    {
        if (IsActive) throw new DomainException("The user is already active.");
        IsActive = true;
        Audit(AuditActions.UserReactivated, actorId, now, new());
    }

    /// <summary>Changing the hash also changes the session stamp, so every existing session of this user ends.</summary>
    public void ResetPassword(string newPasswordHash, Guid actorId, DateTimeOffset now)
    {
        PasswordHash = newPasswordHash;
        Audit(AuditActions.PasswordReset, actorId, now, new()); // never the password or hash
    }

    /// <summary>Sets the hash without an audit record: initial creation only (seeding, tests).</summary>
    public void SetPasswordHash(string passwordHash) => PasswordHash = passwordHash;

    private void Audit(string action, Guid actorId, DateTimeOffset now, Dictionary<string, object?> details) =>
        RaiseAudit(AuditEvent.Create(OrganizationId, nameof(User), Id, action, actorId, now, details));
}
