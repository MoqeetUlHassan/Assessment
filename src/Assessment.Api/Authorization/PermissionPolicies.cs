using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

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

/// <summary>
/// A failed permission policy answers 403 with WHICH permission is missing (ProblemDetails), instead of the
/// framework's bare "Forbidden". Everything else (401 challenges, successes) uses the default behaviour.
/// </summary>
public sealed class PermissionDeniedResultHandler(IProblemDetailsService problemDetails) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        var missing = result.Forbidden
            ? result.AuthorizationFailure?.FailedRequirements.OfType<PermissionRequirement>().FirstOrDefault()
            : null;
        if (missing is null)
        {
            await _default.HandleAsync(next, context, policy, result);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await problemDetails.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = { Status = StatusCodes.Status403Forbidden, Title = $"Your role doesn't have the {missing.Permission} permission." },
        });
    }
}
