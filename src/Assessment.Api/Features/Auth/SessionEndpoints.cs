using Assessment.Api.Authorization;
using Assessment.Api.Infrastructure.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace Assessment.Api.Features.Auth;

public static class SessionEndpoints
{
    public static void MapSession(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync();
            return Results.NoContent();
        }).AllowAnonymous();

        app.MapGet("/api/me", async (CurrentUser me, AppDbContext db, CancellationToken ct) =>
        {
            // Both loads are tenant-filtered: a user can only ever read their own org here.
            var user = await db.Users.AsNoTracking().Include(u => u.Role).SingleAsync(u => u.Id == me.UserId, ct);
            var org = await db.Organizations.AsNoTracking().SingleAsync(ct);
            return Results.Ok(MeResponse.From(user, me.Permissions, org));
        }).RequireAuthorization();
    }
}
