using System.ComponentModel.DataAnnotations;
using Assessment.Api.Authorization;
using Assessment.Api.Domain;
using Assessment.Api.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Assessment.Api.Features.Sites;

public sealed record SiteView(Guid Id, string Name);

/// <summary>The body has no organization field: a new site always belongs to the caller's organization.</summary>
public sealed record CreateSiteBody([property: Required, MaxLength(Site.NameMaxLength)] string Name);

public static class SiteEndpoints
{
    public static void MapSites(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/sites", async (AppDbContext db, CancellationToken ct) =>
                Results.Ok(await db.Sites.AsNoTracking().OrderBy(s => s.Name)
                    .Select(s => new SiteView(s.Id, s.Name)).ToListAsync(ct)))
            .RequireAuthorization();

        // Any signed-in member of the organization can add a site (e.g. from the request form).
        app.MapPost("/api/sites", CreateAsync).RequireAuthorization();
    }

    private static async Task<IResult> CreateAsync(
        CreateSiteBody body, AppDbContext db, CurrentUser me, TimeProvider clock, CancellationToken ct)
    {
        var site = Site.CreateByUser(me.OrganizationId, body.Name, me.UserId, clock.GetUtcNow()); // invalid name → 400

        // Unique per organization, case-insensitively ("site 12" = "Site 12"). The query is tenant-filtered,
        // so another organization's "Site 12" never clashes.
        var lower = site.Name.ToLower();
        if (await db.Sites.AnyAsync(s => s.Name.ToLower() == lower, ct))
            return AlreadyExists(site.Name);

        db.Add(site);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return AlreadyExists(site.Name); // two people adding the same name at the same moment
        }

        return Results.Created($"/api/sites/{site.Id}", new SiteView(site.Id, site.Name));
    }

    private static IResult AlreadyExists(string name) =>
        Results.Problem(statusCode: StatusCodes.Status409Conflict, title: $"A site named \"{name}\" already exists.");
}
