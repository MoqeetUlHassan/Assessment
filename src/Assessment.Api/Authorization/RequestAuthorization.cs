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

    // Each rule returns WHY it refuses (null = allowed), so the UI and 403 responses explain the actual reason
    // and can never disagree with the check itself. Order matters: the most fundamental reason is reported first.

    public static bool CanDecide(CurrentUser user, MaintenanceRequest request) => WhyCannotDecide(user, request) is null;

    public static bool CanModify(CurrentUser user, MaintenanceRequest request) => WhyCannotModify(user, request) is null;

    public static string? WhyCannotDecide(CurrentUser user, MaintenanceRequest request)
    {
        // Org mismatch is already impossible via query filters; asserted anyway.
        if (!user.IsAuthenticated || user.OrganizationId != request.OrganizationId) return "You can't act on this request.";
        if (!user.Has(Permissions.RequestsApprove))
            return "Your role can't approve or reject requests (it doesn't have the requests.approve permission).";
        if (request.RequestedById == user.UserId)
            return "You raised this request, and nobody can approve or reject their own request.";
        if (request.PendingRevision?.SubmittedById == user.UserId)
            return "You submitted this change, and nobody can approve or reject their own change.";
        return null;
    }

    public static string? WhyCannotModify(CurrentUser user, MaintenanceRequest request)
    {
        if (!user.IsAuthenticated || user.OrganizationId != request.OrganizationId) return "You can't act on this request.";
        if (user.Has(Permissions.RequestsManage)) return null;
        if (user.Has(Permissions.RequestsCreate) && request.RequestedById == user.UserId) return null;
        return user.Has(Permissions.RequestsCreate)
            ? "Only the person who raised this request, or someone with the requests.manage permission, can change it."
            : "Your role can't change requests.";
    }
}
