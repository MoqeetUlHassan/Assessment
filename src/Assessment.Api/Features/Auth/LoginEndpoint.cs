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
    public const string ChallengeRateLimitPolicy = "password-challenge";

    // Verified against when the email is unknown, so response time doesn't reveal which emails exist.
    private static readonly string DummyHash = new PasswordHasher<User>().HashPassword(null!, Guid.NewGuid().ToString());

    public sealed record PasswordChallenge(string KeyId, string Algorithm, string PublicKey, string Nonce, int ExpiresInSeconds);

    /// <summary>
    /// The password is never sent in plain text in the payload: only <see cref="EncryptedPassword"/>
    /// (see <see cref="PasswordFieldEncryption"/>). A plain "password" field is not part of the contract.
    /// </summary>
    public sealed record LoginRequest(
        [property: Required, MaxLength(254)] string Email,
        [property: Required, MaxLength(64)] string KeyId,
        [property: Required, MaxLength(2048)] string EncryptedPassword);

    public static void MapLogin(this IEndpointRouteBuilder app)
    {
        // One challenge per password field sent: login, and the admin create-user / reset-password forms.
        app.MapGet("/api/auth/password-challenge", (PasswordFieldEncryption encryption) =>
            {
                var nonce = encryption.IssueNonce();
                return nonce is null
                    ? Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Too many pending logins; try again shortly.")
                    : Results.Ok(new PasswordChallenge(encryption.KeyId, PasswordFieldEncryption.Algorithm,
                        encryption.PublicKeySpkiBase64, nonce, (int)PasswordFieldEncryption.NonceLifetime.TotalSeconds));
            })
            .AllowAnonymous()
            .RequireRateLimiting(ChallengeRateLimitPolicy);

        app.MapPost("/api/auth/login", HandleAsync)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicy);
    }

    private static async Task<IResult> HandleAsync(
        LoginRequest body, AppDbContext db, IPasswordHasher<User> hasher, PasswordFieldEncryption encryption,
        HttpContext http, ILogger<LoginRequest> logger, CancellationToken ct)
    {
        // Decrypt first: a wrong key, tampered ciphertext or replayed/expired challenge is rejected before any lookup.
        var password = encryption.TryDecryptPassword(body.KeyId, body.EncryptedPassword);
        if (password is null)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "The login challenge is invalid or has expired. Reload the page and try again.");

        string email;
        try { email = User.NormalizeEmail(body.Email); }
        catch (DomainException) { return InvalidCredentials(); }

        // The ONE sanctioned bypass of tenant filters (allow-listed in TenantModelConventionTests):
        // no tenant is known until the user is found. Everything after sign-in is filtered normally.
        var user = await db.Users.IgnoreQueryFilters().Include(u => u.Role)
            .SingleOrDefaultAsync(u => u.Email == email, ct);

        var verification = hasher.VerifyHashedPassword(user!, user?.PasswordHash ?? DummyHash, password);
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
