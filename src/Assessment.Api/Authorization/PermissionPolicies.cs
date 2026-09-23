using Microsoft.AspNetCore.Authorization;

namespace Assessment.Api.Authorization;

/// <summary>Requires a permission from the catalog. Policies are named after the permission itself.</summary>
public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

public sealed class PermissionHandler(CurrentUser currentUser) : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (currentUser.IsAuthenticated && currentUser.Has(requirement.Permission))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}

public static class PermissionPolicyExtensions
{
    /// <summary>One policy per catalog permission, e.g. <c>.RequireAuthorization(Permissions.RequestsApprove)</c>.</summary>
    public static AuthorizationBuilder AddPermissionPolicies(this AuthorizationBuilder builder)
    {
        foreach (var permission in Domain.Permissions.All)
        {
            builder.AddPolicy(permission, policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new PermissionRequirement(permission)));
        }
        return builder;
    }
}
