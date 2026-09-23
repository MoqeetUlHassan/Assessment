using Assessment.Api.Domain;
using Assessment.Api.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Assessment.Api.Features.Reports;

public sealed record SiteSpend(Guid SiteId, string SiteName, decimal TotalSpend, int CompletedCount);

public sealed record SpendReport(
    DateOnly From, DateOnly To, IReadOnlyList<SiteSpend> Sites, decimal TotalSpend, int CompletedCount);

/// <summary>
/// Total spend per site over a date range, for the caller's organization only.
///
/// Definitions (DECISIONS.md):
/// - spend = SUM(actual_cost) of COMPLETED requests; estimates and unfinished work are not spend;
/// - a request counts on the day it completed (completed_at), in UTC;
/// - from/to are inclusive dates, evaluated as the half-open range [from 00:00, to+1 00:00) so no instant is lost;
/// - every site of the organization is listed, including sites with zero spend.
/// Tenant scoping comes from the global query filters on both sites and requests.
/// </summary>
public static class SpendReportEndpoint
{
    public const int MaxRangeDays = 366;

    public static void MapSpendReport(this IEndpointRouteBuilder app) =>
        app.MapGet("/api/reports/spend", HandleAsync).RequireAuthorization(Permissions.ReportsSpend);

    private static async Task<IResult> HandleAsync(DateOnly? from, DateOnly? to, AppDbContext db, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if (from is null) errors["from"] = ["Required (yyyy-MM-dd)."];
        if (to is null) errors["to"] = ["Required (yyyy-MM-dd)."];
        if (from is not null && to is not null)
        {
            if (from > to) errors["to"] = ["Must be on or after 'from'."];
            else if (to.Value.DayNumber - from.Value.DayNumber + 1 > MaxRangeDays)
                errors["to"] = [$"The range can be at most {MaxRangeDays} days."];
        }
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var start = new DateTimeOffset(from!.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var endExclusive = new DateTimeOffset(to!.Value.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var sites = await Query(db, start, endExclusive).ToListAsync(ct);

        return Results.Ok(new SpendReport(from.Value, to.Value, sites, sites.Sum(s => s.TotalSpend), sites.Sum(s => s.CompletedCount)));
    }

    /// <summary>
    /// One SQL statement: each site with correlated aggregates over its completed requests in range.
    /// Served by the partial covering index ix_maintenance_requests_spend_report.
    /// </summary>
    internal static IQueryable<SiteSpend> Query(AppDbContext db, DateTimeOffset start, DateTimeOffset endExclusive)
    {
        var completedInRange = db.MaintenanceRequests.Where(r =>
            r.Status == RequestStatus.Completed && r.CompletedAt >= start && r.CompletedAt < endExclusive);

        return db.Sites.AsNoTracking()
            .OrderBy(s => s.Name)
            .Select(s => new SiteSpend(
                s.Id,
                s.Name,
                completedInRange.Where(r => r.SiteId == s.Id).Sum(r => r.ActualCost) ?? 0m,
                completedInRange.Count(r => r.SiteId == s.Id)));
    }
}
