using Assessment.Api.Domain;
using Microsoft.AspNetCore.Identity;

namespace Assessment.Api.Tests.Persistence;

/// <summary>
/// A fresh organization per test: tests never share tenant data, so no cleanup is needed
/// (and audit rows could not be deleted anyway).
/// </summary>
internal sealed record TestTenant(
    Organization Org, Site Site, Role ApproverRole, User Admin, User Requester, User Approver, User Approver2)
{
    public const string Password = "Test-Password-123";
    private static readonly PasswordHasher<User> Hasher = new();

    public static async Task<TestTenant> CreateAsync(ApiFactory factory, decimal threshold = 10_000m)
    {
        using var scope = DbScope.System(factory);
        var tag = Guid.NewGuid().ToString("N")[..12];
        var org = Organization.Create($"Org {tag}", threshold);
        var site = Site.Create(org.Id, "Site 12");
        var adminRole = Role.CreateSystemAdmin(org.Id);
        var approverRole = Role.Create(org.Id, "Approver",
            [Permissions.RequestsCreate, Permissions.RequestsManage, Permissions.RequestsApprove, Permissions.ReportsSpend]);
        var requesterRole = Role.Create(org.Id, "Requester", [Permissions.RequestsCreate]);

        User NewUser(Role role, string local, string name)
        {
            var user = User.Create(org.Id, role.Id, $"{local}-{tag}@test.local", name, "pending");
            user.SetPasswordHash(Hasher.HashPassword(user, Password));
            return user;
        }

        var admin = NewUser(adminRole, "admin", "Ada Admin");
        var requester = NewUser(requesterRole, "req", "Rita Requester");
        var approver = NewUser(approverRole, "app", "Andy Approver");
        var approver2 = NewUser(approverRole, "app2", "Bea Approver");

        scope.Db.AddRange(org, site, adminRole, approverRole, requesterRole, admin, requester, approver, approver2);
        await scope.Db.SaveChangesAsync();
        return new TestTenant(org, site, approverRole, admin, requester, approver, approver2);
    }

    public DbScope AsRequester(ApiFactory factory) => DbScope.As(factory, Org.Id, Requester.Id);
    public DbScope AsApprover(ApiFactory factory) => DbScope.As(factory, Org.Id, Approver.Id);
    public DbScope AsAdmin(ApiFactory factory) => DbScope.As(factory, Org.Id, Admin.Id);

    public MaintenanceRequest Raise(decimal estimate, DateTimeOffset now) =>
        MaintenanceRequest.Raise(Org.Id, Site.Id, Requester.Id, "Fix boiler", estimate, Org.ApprovalThreshold, now);

    /// <summary>Raises and saves a request as the requester.</summary>
    public async Task<MaintenanceRequest> SaveNewRequestAsync(ApiFactory factory, decimal estimate, DateTimeOffset now)
    {
        using var scope = AsRequester(factory);
        var request = Raise(estimate, now);
        scope.Db.Add(request);
        await scope.Db.SaveChangesAsync();
        return request;
    }

    public static string HashPassword(User user, string password) => Hasher.HashPassword(user, password);
}
