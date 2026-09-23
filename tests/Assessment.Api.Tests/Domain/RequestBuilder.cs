using Assessment.Api.Domain;

namespace Assessment.Api.Tests.Domain;

/// <summary>Drives a request into each reachable state through the public API only (no reflection).</summary>
internal static class RequestBuilder
{
    public const decimal Threshold = 10_000m;
    public static readonly DateTimeOffset Now = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    public static readonly Guid Org = Guid.CreateVersion7();
    public static readonly Guid SiteId = Guid.CreateVersion7();
    public static readonly Guid Requester = Guid.CreateVersion7();
    public static readonly Guid Approver = Guid.CreateVersion7();

    public static MaintenanceRequest Raise(decimal estimate, string description = "Fix boiler", decimal threshold = Threshold) =>
        MaintenanceRequest.Raise(Org, SiteId, Requester, description, estimate, threshold, Now);

    public static MaintenanceRequest InState(string state) => state switch
    {
        "Approved" => Raise(5_000m),
        "Pending(Initial)" => Raise(15_000m),
        "Pending(Edit)" => Edited(Raise(5_000m), 15_000m),
        "Pending(ActualCost)" => WithActual(Raise(5_000m), 12_000m),
        "Rejected" => Rejected(Raise(15_000m)),
        "Completed" => WithActual(Raise(5_000m), 500m),
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static MaintenanceRequest Edited(MaintenanceRequest r, decimal estimate, string? description = null)
    {
        r.Edit(Requester, description ?? r.Description, estimate, "scope changed", Now);
        return r;
    }

    public static MaintenanceRequest WithActual(MaintenanceRequest r, decimal actual)
    {
        r.SubmitActualCost(Requester, actual, null, Now);
        return r;
    }

    public static MaintenanceRequest Approved(MaintenanceRequest r)
    {
        r.Approve(Approver, r.PendingRevision!.Id, null, Now);
        return r;
    }

    public static MaintenanceRequest Rejected(MaintenanceRequest r)
    {
        r.Reject(Approver, r.PendingRevision!.Id, "too expensive", Now);
        return r;
    }
}
