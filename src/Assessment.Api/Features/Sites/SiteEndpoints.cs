using Assessment.Api.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Assessment.Api.Features.Sites;

public sealed record SiteView(Guid Id, string Name);

public static class SiteEndpoints
{
    public static void MapSites(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/sites", async (AppDbContext db, CancellationToken ct) =>
                Results.Ok(await db.Sites.AsNoTracking().OrderBy(s => s.Name)
                    .Select(s => new SiteView(s.Id, s.Name)).ToListAsync(ct)))
            .RequireAuthorization();
}
