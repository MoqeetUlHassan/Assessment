using Assessment.Api.Domain;
using Assessment.Api.Infrastructure.Data;

namespace Assessment.Api.Tests.Persistence;

/// <summary>
/// A fresh organization per test: tests never share tenant data, so no cleanup is needed
/// (and audit rows could not be deleted anyway).
/// </summary>
internal sealed record TestTenant(Organization Org, Site Site, User Requester, User Approver)
{
    public static async Task<TestTenant> CreateAsync(AppDbContext db, decimal threshold = 10_000m)
    {
        var tag = Guid.NewGuid().ToString("N")[..12];
        var org = Organization.Create($"Org {tag}", threshold);
        var site = Site.Create(org.Id, "Site 12");
        var approverRole = Role.Create(org.Id, "Approver",
            [Permissions.RequestsCreate, Permissions.RequestsManage, Permissions.RequestsApprove, Permissions.ReportsSpend]);
        var requesterRole = Role.Create(org.Id, "Requester", [Permissions.RequestsCreate]);
        var requester = User.Create(org.Id, requesterRole.Id, $"req-{tag}@test.local", "Rita Requester", "not-a-real-hash");
        var approver = User.Create(org.Id, approverRole.Id, $"app-{tag}@test.local", "Andy Approver", "not-a-real-hash");

        db.AddRange(org, site, approverRole, requesterRole, requester, approver);
        await db.SaveChangesAsync();
        return new TestTenant(org, site, requester, approver);
    }

    public MaintenanceRequest Raise(decimal estimate, DateTimeOffset now) =>
        MaintenanceRequest.Raise(Org.Id, Site.Id, Requester.Id, "Fix boiler", estimate, Org.ApprovalThreshold, now);
}
