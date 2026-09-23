using System.ComponentModel.DataAnnotations;
using Assessment.Api.Authorization;
using Assessment.Api.Domain;
using Assessment.Api.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Assessment.Api.Features.Auth;

public static class LoginEndpoint
{
    public const string RateLimitPolicy = "login";

    // Verified against when the email is unknown, so response time doesn't reveal which emails exist.
    private static readonly string DummyHash = new PasswordHasher<User>().HashPassword(null!, Guid.NewGuid().ToString());

    public sealed record LoginRequest(
        [property: Required, MaxLength(254)] string Email,
        [property: Required, MaxLength(200)] string Password);

    public static void MapLogin(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/auth/login", HandleAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy);

    private static async Task<IResult> HandleAsync(
        LoginRequest body, AppDbContext db, IPasswordHasher<User> hasher, HttpContext http,
        ILogger<LoginRequest> logger, CancellationToken ct)
    {
        string email;
        try { email = User.NormalizeEmail(body.Email); }
        catch (DomainException) { return InvalidCredentials(); }

        // The ONE sanctioned bypass of tenant filters (allow-listed in TenantModelConventionTests):
        // no tenant is known until the user is found. Everything after sign-in is filtered normally.
        var user = await db.Users.IgnoreQueryFilters().Include(u => u.Role)
            .SingleOrDefaultAsync(u => u.Email == email, ct);

        var verification = hasher.VerifyHashedPassword(user!, user?.PasswordHash ?? DummyHash, body.Password);
        if (user is null || !user.IsActive || verification == PasswordVerificationResult.Failed)
        {
            logger.LogWarning("Failed login for {Email} from {Ip}", email, http.Connection.RemoteIpAddress);
            return InvalidCredentials();
        }

        var org = await db.Organizations.IgnoreQueryFilters().SingleAsync(o => o.Id == user.OrganizationId, ct);

        const string scheme = CookieAuthenticationDefaults.AuthenticationScheme;
        await http.SignInAsync(scheme, SessionClaims.Create(user, scheme));

        IEnumerable<string> permissions = user.Role.IsSystemAdmin ? Permissions.All : user.Role.Permissions;
        return Results.Ok(MeResponse.From(user, permissions, org));
    }

    // Same response for unknown email, wrong password and deactivated account: no account enumeration.
    private static IResult InvalidCredentials() =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid email or password.");
}
