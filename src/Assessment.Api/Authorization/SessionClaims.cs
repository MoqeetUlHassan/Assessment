using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Assessment.Api.Domain;

namespace Assessment.Api.Authorization;

/// <summary>
/// What the session cookie carries: who, which org, and a stamp that changes when the password changes.
/// Deliberately no permissions or role: those are loaded from the database on every request.
/// </summary>
public static class SessionClaims
{
    public const string UserId = "uid";
    public const string OrganizationId = "org";
    public const string Stamp = "sst";

    public static ClaimsPrincipal Create(User user, string authenticationType) =>
        new(new ClaimsIdentity(
        [
            new Claim(UserId, user.Id.ToString()),
            new Claim(OrganizationId, user.OrganizationId.ToString()),
            new Claim(Stamp, StampFor(user)),
        ], authenticationType));

    public static bool TryRead(ClaimsPrincipal principal, out Guid userId, out Guid organizationId, out string stamp)
    {
        stamp = principal.FindFirstValue(Stamp) ?? "";
        organizationId = Guid.Empty;
        return Guid.TryParse(principal.FindFirstValue(UserId), out userId)
               && Guid.TryParse(principal.FindFirstValue(OrganizationId), out organizationId)
               && stamp.Length > 0;
    }

    /// <summary>
    /// Derived from the password hash, so resetting a password invalidates every existing session
    /// without an extra column. Not secret: it only has to change when the hash changes.
    /// </summary>
    public static string StampFor(User user) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(user.PasswordHash)))[..16];
}
