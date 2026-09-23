using Assessment.Api.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;

namespace Assessment.Api.Authorization;

/// <summary>Resource-based operations on a loaded MaintenanceRequest.</summary>
public static class RequestOperations
{
    /// <summary>Approve or reject the pending revision.</summary>
    public static readonly OperationAuthorizationRequirement Decide = new() { Name = nameof(Decide) };

    /// <summary>Edit content or submit the actual cost.</summary>
    public static readonly OperationAuthorizationRequirement Modify = new() { Name = nameof(Modify) };
}

/// <summary>
/// Permission rules that need the record itself. The conflict-of-interest rule lives here, as a permission,
/// and applies to every user including OrgAdmin: holding requests.approve never covers your own request
/// or a revision you submitted. (Backed by a DB CHECK on request_revisions.)
/// </summary>
public sealed class MaintenanceRequestAuthorizationHandler(CurrentUser currentUser)
    : AuthorizationHandler<OperationAuthorizationRequirement, MaintenanceRequest>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, OperationAuthorizationRequirement requirement, MaintenanceRequest request)
    {
        var allowed = requirement.Name switch
        {
            nameof(RequestOperations.Decide) => CanDecide(currentUser, request),
            nameof(RequestOperations.Modify) => CanModify(currentUser, request),
            _ => false,
        };
        if (allowed) context.Succeed(requirement);
        return Task.CompletedTask;
    }

    public static bool CanDecide(CurrentUser user, MaintenanceRequest request) =>
        user.IsAuthenticated
        && user.OrganizationId == request.OrganizationId // already guaranteed by query filters; asserted anyway
        && user.Has(Permissions.RequestsApprove)
        && request.RequestedById != user.UserId
        && request.PendingRevision?.SubmittedById != user.UserId;

    public static bool CanModify(CurrentUser user, MaintenanceRequest request) =>
        user.IsAuthenticated
        && user.OrganizationId == request.OrganizationId
        && (user.Has(Permissions.RequestsManage)
            || (user.Has(Permissions.RequestsCreate) && request.RequestedById == user.UserId));
}
