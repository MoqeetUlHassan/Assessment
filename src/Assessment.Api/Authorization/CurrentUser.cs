using Assessment.Api.Domain;

namespace Assessment.Api.Authorization;

/// <summary>
/// The signed-in user for this request, loaded fresh from the database by SessionValidationMiddleware.
/// Permissions come from the user's role as it is NOW, not as it was at login.
/// </summary>
public sealed class CurrentUser
{
    public bool IsAuthenticated { get; private set; }
    public Guid UserId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public string DisplayName { get; private set; } = "";
    public string Email { get; private set; } = "";
    public string RoleName { get; private set; } = "";
    public IReadOnlySet<string> Permissions { get; private set; } = new HashSet<string>();

    public bool Has(string permission) => Permissions.Contains(permission);

    public void Initialize(User user, Role role)
    {
        IsAuthenticated = true;
        UserId = user.Id;
        OrganizationId = user.OrganizationId;
        DisplayName = user.DisplayName;
        Email = user.Email;
        RoleName = role.Name;
        Permissions = role.IsSystemAdmin
            ? Domain.Permissions.All
            : role.Permissions.ToHashSet();
    }
}
