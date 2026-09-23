using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Assessment.Api.Tests.Persistence;

namespace Assessment.Api.Tests.Http;

// Admin endpoints are where privilege escalation and cross-tenant writes would happen, so the tests
// target exactly that: who can call them, where created things land, what can't be granted, and that
// every admin action is attributable in the audit log.
public class AdminHttpTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly string[] AdminEndpoints =
    [
        "GET /api/admin/users", "POST /api/admin/users", "GET /api/admin/roles", "POST /api/admin/roles",
        "GET /api/admin/permissions", "PUT /api/admin/settings/threshold", "GET /api/admin/audit",
    ];

    [Fact]
    public async Task Non_admins_get_403_on_every_admin_endpoint()
    {
        var t = await TestTenant.CreateAsync(factory);
        var approver = await factory.SignedInClientAsync(t.Approver); // has every non-admin permission

        foreach (var endpoint in AdminEndpoints)
        {
            var (method, path) = (endpoint.Split(' ')[0], endpoint.Split(' ')[1]);
            var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method != "GET") request.Content = JsonContent.Create(new { });
            var response = await approver.SendAsync(request);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{endpoint} returned {response.StatusCode}");
        }
    }

    [Fact]
    public async Task Created_user_lands_in_the_admins_organization_and_can_sign_in()
    {
        var t = await TestTenant.CreateAsync(factory);
        var other = await TestTenant.CreateAsync(factory);
        var admin = await factory.SignedInClientAsync(t.Admin);
        var email = $"new-{Guid.NewGuid():N}@test.local";

        // An organizationId in the body is not part of the contract and must be ignored.
        var created = await admin.PostAsJsonAsync("/api/admin/users", new
        {
            displayName = "Nina New", email, password = "Nina-Password-123", roleId = t.ApproverRole.Id,
            organizationId = other.Org.Id,
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var login = await factory.CreateClient().LoginAsync(email, "Nina-Password-123");
        var me = await login.JsonAsync();
        Assert.Equal(t.Org.Id, me.GetProperty("organization").GetProperty("id").GetGuid());
        Assert.Equal("Approver", me.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Another_organizations_role_cannot_be_assigned()
    {
        var t = await TestTenant.CreateAsync(factory);
        var other = await TestTenant.CreateAsync(factory);
        var admin = await factory.SignedInClientAsync(t.Admin);

        var create = await admin.PostAsJsonAsync("/api/admin/users", new
            { displayName = "X", email = $"x-{Guid.NewGuid():N}@test.local", password = "Long-Enough-123", roleId = other.ApproverRole.Id });
        var change = await admin.PutAsJsonAsync($"/api/admin/users/{t.Requester.Id}/role", new { roleId = other.ApproverRole.Id });

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
    }

    [Fact]
    public async Task Admin_actions_on_another_organizations_users_and_roles_are_404()
    {
        var victim = await TestTenant.CreateAsync(factory);
        var attacker = await TestTenant.CreateAsync(factory);
        var admin = await factory.SignedInClientAsync(attacker.Admin);
        var victimUser = victim.Approver.Id;

        var attempts = new Dictionary<string, HttpResponseMessage>
        {
            ["deactivate"] = await admin.PostAsync($"/api/admin/users/{victimUser}/deactivate", null),
            ["password"] = await admin.PostAsJsonAsync($"/api/admin/users/{victimUser}/password", new { password = "Taken-Over-12345" }),
            ["role"] = await admin.PutAsJsonAsync($"/api/admin/users/{victimUser}/role", new { roleId = attacker.ApproverRole.Id }),
            ["permissions"] = await admin.PutAsJsonAsync($"/api/admin/roles/{victim.ApproverRole.Id}/permissions", new { permissions = new[] { "requests.create" } }),
        };
        Assert.All(attempts, a => Assert.True(a.Value.StatusCode == HttpStatusCode.NotFound, $"{a.Key} returned {a.Value.StatusCode}"));

        // The victim's account still works with its original password.
        await factory.SignedInClientAsync(victim.Approver);
    }

    [Fact]
    public async Task Admin_cannot_deactivate_or_demote_themselves()
    {
        var t = await TestTenant.CreateAsync(factory);
        var admin = await factory.SignedInClientAsync(t.Admin);

        var deactivate = await admin.PostAsync($"/api/admin/users/{t.Admin.Id}/deactivate", null);
        var demote = await admin.PutAsJsonAsync($"/api/admin/users/{t.Admin.Id}/role", new { roleId = t.ApproverRole.Id });

        Assert.Equal(HttpStatusCode.BadRequest, deactivate.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, demote.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/users")).StatusCode); // still an admin
    }

    [Fact]
    public async Task Admin_permissions_cannot_be_granted_and_the_orgadmin_role_cannot_be_edited()
    {
        var t = await TestTenant.CreateAsync(factory);
        var admin = await factory.SignedInClientAsync(t.Admin);
        var roles = await admin.GetFromJsonAsync<JsonElement>("/api/admin/roles");
        var orgAdminRole = roles.EnumerateArray().Single(r => r.GetProperty("isSystemAdmin").GetBoolean()).GetProperty("id").GetGuid();

        var grant = await admin.PutAsJsonAsync($"/api/admin/roles/{t.ApproverRole.Id}/permissions",
            new { permissions = new[] { "requests.approve", "admin.users" } });
        var newRole = await admin.PostAsJsonAsync("/api/admin/roles",
            new { name = "Shadow admin", permissions = new[] { "admin.settings" } });
        var editAdmin = await admin.PutAsJsonAsync($"/api/admin/roles/{orgAdminRole}/permissions",
            new { permissions = new[] { "requests.create" } });

        Assert.Equal(HttpStatusCode.BadRequest, grant.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, newRole.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, editAdmin.StatusCode);
    }

    [Fact]
    public async Task Duplicate_email_is_a_conflict_even_across_organizations()
    {
        var t = await TestTenant.CreateAsync(factory);
        var other = await TestTenant.CreateAsync(factory);
        var admin = await factory.SignedInClientAsync(t.Admin);

        var response = await admin.PostAsJsonAsync("/api/admin/users", new
            { displayName = "Dup", email = other.Requester.Email.ToUpperInvariant(), password = "Long-Enough-123", roleId = t.ApproverRole.Id });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Threshold_change_applies_only_to_requests_raised_afterwards()
    {
        var t = await TestTenant.CreateAsync(factory); // threshold 10,000
        var admin = await factory.SignedInClientAsync(t.Admin);
        var requester = await factory.SignedInClientAsync(t.Requester);

        var before = await requester.CreateRequestAsync(t.Site.Id, 15_000m);
        Assert.Equal("PendingApproval", before.Status());

        var change = await admin.PutAsJsonAsync("/api/admin/settings/threshold", new { amount = 20_000m, reason = "Budget review" });
        Assert.Equal(20_000m, (await change.JsonAsync()).GetProperty("approvalThreshold").GetDecimal());

        var after = await requester.CreateRequestAsync(t.Site.Id, 15_000m);
        Assert.Equal("Approved", after.Status()); // new threshold
        var stillPending = await requester.GetFromJsonAsync<JsonElement>($"/api/requests/{before.Id()}");
        Assert.Equal("PendingApproval", stillPending.Status()); // not retroactively auto-approved
        Assert.Equal(10_000m, stillPending.GetProperty("approvalThreshold").GetDecimal());
    }

    [Fact]
    public async Task Every_admin_action_is_in_the_audit_log_with_the_acting_admin()
    {
        var t = await TestTenant.CreateAsync(factory);
        var admin = await factory.SignedInClientAsync(t.Admin);

        await admin.PutAsJsonAsync("/api/admin/settings/threshold", new { amount = 12_000m, reason = "Raise" });
        var created = await (await admin.PostAsJsonAsync("/api/admin/users", new
            { displayName = "Sam Sock", email = $"sam-{Guid.NewGuid():N}@test.local", password = "Sam-Password-123", roleId = t.ApproverRole.Id })).JsonAsync();
        await admin.PostAsync($"/api/admin/users/{created.Id()}/deactivate", null);
        await admin.PostAsJsonAsync($"/api/admin/users/{t.Requester.Id}/password", new { password = "Reset-Password-123" });
        await admin.PutAsJsonAsync($"/api/admin/roles/{t.ApproverRole.Id}/permissions", new { permissions = new[] { "requests.create", "requests.approve" } });

        var log = await admin.GetFromJsonAsync<JsonElement>("/api/admin/audit");
        var entries = log.GetProperty("items").EnumerateArray()
            .Select(e => (Action: e.GetProperty("action").GetString(), Actor: e.GetProperty("actorName").GetString(), Details: e.GetProperty("details").GetRawText()))
            .ToList();

        foreach (var action in new[] { "ThresholdChanged", "UserCreated", "UserDeactivated", "PasswordReset", "RolePermissionsChanged" })
            Assert.Contains(entries, e => e.Action == action && e.Actor == "Ada Admin");
        Assert.DoesNotContain(entries, e => e.Details.Contains("Reset-Password-123") || e.Details.Contains("Sam-Password-123"));

        // The account the admin created is attributable to them (the sock-puppet control from DECISIONS.md).
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/admin/users");
        var sam = users.EnumerateArray().Single(u => u.GetProperty("id").GetGuid() == created.Id());
        Assert.Equal("Ada Admin", sam.GetProperty("createdByName").GetString());
    }
}
