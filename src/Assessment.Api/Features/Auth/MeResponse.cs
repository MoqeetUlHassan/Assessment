using Assessment.Api.Domain;

namespace Assessment.Api.Features.Auth;

public sealed record OrganizationSummary(Guid Id, string Name, decimal ApprovalThreshold);

public sealed record MeResponse(
    Guid UserId, string DisplayName, string Email, string Role, IReadOnlyList<string> Permissions,
    OrganizationSummary Organization)
{
    public static MeResponse From(User user, IEnumerable<string> permissions, Organization org) => new(
        user.Id, user.DisplayName, user.Email, user.Role.Name, [.. permissions.Order()],
        new OrganizationSummary(org.Id, org.Name, org.ApprovalThreshold));
}
