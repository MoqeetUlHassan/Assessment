using System.Security.Claims;
using Assessment.Api.Infrastructure.Data;
using Assessment.Api.Infrastructure.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace Assessment.Api.Authorization;

/// <summary>
/// Runs on every authenticated request, after cookie authentication:
/// 1. establishes the tenant from the cookie's org id (which was taken from the user's DB record at login);
/// 2. reloads the user and role, so deactivation, role changes and permission edits apply immediately;
/// 3. drops the session if the user is gone, inactive, or their password changed since login.
/// A dropped session continues as anonymous, so protected endpoints return 401.
/// </summary>
public sealed class SessionValidationMiddleware(RequestDelegate next, ILogger<SessionValidationMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, TenantContext tenant, CurrentUser currentUser, AppDbContext db)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            if (SessionClaims.TryRead(context.User, out var userId, out var organizationId, out var stamp))
            {
                tenant.Set(organizationId, userId);
                var user = await db.Users.AsNoTracking().Include(u => u.Role)
                    .SingleOrDefaultAsync(u => u.Id == userId, context.RequestAborted);

                if (user is { IsActive: true } && SessionClaims.StampFor(user) == stamp)
                {
                    currentUser.Initialize(user, user.Role);
                    await next(context);
                    return;
                }

                logger.LogInformation("Session for user {UserId} rejected (missing, inactive or password changed)", userId);
                tenant.Clear();
            }

            await context.SignOutAsync();
            context.User = new ClaimsPrincipal(new ClaimsIdentity());
        }

        await next(context);
    }
}
