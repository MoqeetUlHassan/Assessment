using System.ComponentModel.DataAnnotations;

namespace Assessment.Api.Features.Admin;

public sealed record CreateUserBody(
    [property: Required, MaxLength(200)] string DisplayName,
    [property: Required, MaxLength(254)] string Email,
    [property: Required, MinLength(12), MaxLength(200)] string Password,
    [property: Required] Guid? RoleId);

public sealed record ChangeRoleBody([property: Required] Guid? RoleId);

public sealed record ResetPasswordBody([property: Required, MinLength(12), MaxLength(200)] string Password);

public sealed record CreateRoleBody(
    [property: Required, MaxLength(100)] string Name,
    [property: Required] string[] Permissions);

public sealed record ChangePermissionsBody([property: Required] string[] Permissions);

public sealed record ChangeThresholdBody(
    [property: Range(0, 10_000_000)] decimal Amount,
    [property: Required, MaxLength(1000)] string Reason);

public sealed record UserView(
    Guid Id, string DisplayName, string Email, Guid RoleId, string RoleName, bool IsActive,
    DateTimeOffset CreatedAt, string? CreatedByName);

public sealed record RoleView(Guid Id, string Name, bool IsSystemAdmin, IReadOnlyList<string> Permissions, int UserCount);

public sealed record PermissionView(string Name, string Description, bool AdminOnly);

public sealed record OrgAuditEntry(
    long Id, string EntityType, Guid EntityId, string Action, string? FromStatus, string? ToStatus,
    string ActorName, IReadOnlyDictionary<string, object?> Details, DateTimeOffset At);
