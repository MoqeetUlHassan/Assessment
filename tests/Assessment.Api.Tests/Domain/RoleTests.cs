using Assessment.Api.Domain;

namespace Assessment.Api.Tests.Domain;

// Privilege escalation through role editing would bypass every other permission check.
public class RoleTests
{
    private static readonly Guid Org = Guid.CreateVersion7();

    [Theory]
    [InlineData(Permissions.AdminUsers)]
    [InlineData(Permissions.AdminRoles)]
    [InlineData(Permissions.AdminSettings)]
    public void Admin_permissions_cannot_be_granted_to_other_roles(string adminPermission)
    {
        Assert.Throws<DomainException>(() => Role.Create(Org, "Supervisor", [Permissions.RequestsApprove, adminPermission]));

        var role = Role.Create(Org, "Supervisor", [Permissions.RequestsApprove]);
        Assert.Throws<DomainException>(() => role.SetPermissions([adminPermission]));
    }

    [Fact]
    public void OrgAdmin_role_holds_everything_and_cannot_be_edited()
    {
        var admin = Role.CreateSystemAdmin(Org);

        Assert.Equal(Permissions.All.Order(), admin.Permissions);
        Assert.Throws<DomainException>(() => admin.SetPermissions([Permissions.RequestsCreate]));
    }

    [Fact]
    public void Unknown_permission_names_are_refused()
    {
        Assert.Throws<DomainException>(() => Role.Create(Org, "Custom", ["requests.delete"]));
    }
}
