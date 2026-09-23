using Assessment.Api.Authorization;
using Assessment.Api.Domain;

namespace Assessment.Api.Tests.Authorization;

// The conflict-of-interest rules, tested at the permission layer where they live (not the entity).
// Every "who" is checked against both a first-time request and a revision submitted by someone else,
// because "not your request" and "not your revision" are separate rules and either could be dropped.
public class RequestAuthorizationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Org = Guid.CreateVersion7();

    private static readonly Role AdminRole = Role.CreateSystemAdmin(Org);
    private static readonly Role ApproverRole = Role.Create(Org, "Approver",
        [Permissions.RequestsCreate, Permissions.RequestsManage, Permissions.RequestsApprove]);
    private static readonly Role RequesterRole = Role.Create(Org, "Requester", [Permissions.RequestsCreate]);

    private static readonly User Requester = NewUser(RequesterRole);
    private static readonly User OtherRequester = NewUser(RequesterRole);
    private static readonly User Approver = NewUser(ApproverRole);
    private static readonly User OtherApprover = NewUser(ApproverRole);
    private static readonly User Admin = NewUser(AdminRole);

    private static User NewUser(Role role) =>
        User.Create(Org, role.Id, $"{Guid.NewGuid():N}@test.local", "Test User", "hash");

    private static CurrentUser As(User user, Role role)
    {
        var current = new CurrentUser();
        current.Initialize(user, role);
        return current;
    }

    private static MaintenanceRequest PendingRequestBy(User requester) =>
        MaintenanceRequest.Raise(Org, Guid.CreateVersion7(), requester.Id, "Fix boiler", 15_000m, 10_000m, Now);

    [Fact]
    public void Another_approver_can_decide()
    {
        Assert.True(MaintenanceRequestAuthorizationHandler.CanDecide(As(Approver, ApproverRole), PendingRequestBy(Requester)));
    }

    [Fact]
    public void Requester_without_approve_permission_cannot_decide()
    {
        Assert.False(MaintenanceRequestAuthorizationHandler.CanDecide(As(OtherRequester, RequesterRole), PendingRequestBy(Requester)));
    }

    [Fact]
    public void Approver_cannot_decide_their_own_request()
    {
        Assert.False(MaintenanceRequestAuthorizationHandler.CanDecide(As(Approver, ApproverRole), PendingRequestBy(Approver)));
    }

    [Fact]
    public void OrgAdmin_with_every_permission_still_cannot_decide_their_own_request()
    {
        var own = PendingRequestBy(Admin);
        Assert.False(MaintenanceRequestAuthorizationHandler.CanDecide(As(Admin, AdminRole), own));
        Assert.True(MaintenanceRequestAuthorizationHandler.CanDecide(As(Admin, AdminRole), PendingRequestBy(Requester)));
    }

    [Fact]
    public void Approver_cannot_decide_a_revision_they_submitted_on_someone_elses_request()
    {
        var request = MaintenanceRequest.Raise(Org, Guid.CreateVersion7(), Requester.Id, "Fix boiler", 5_000m, 10_000m, Now);
        request.Edit(Approver.Id, "Replace boiler", 15_000m, "scope grew", Now); // Approver authored the pending revision

        Assert.False(MaintenanceRequestAuthorizationHandler.CanDecide(As(Approver, ApproverRole), request));
        Assert.True(MaintenanceRequestAuthorizationHandler.CanDecide(As(OtherApprover, ApproverRole), request));
    }

    [Fact]
    public void Approver_cannot_approve_a_colleagues_edit_of_their_own_request()
    {
        // Added after a planted bug survived: in every other test the owner also authored the pending
        // revision, so the own-revision rule masked the own-request rule. Here only the latter applies.
        var request = MaintenanceRequest.Raise(Org, Guid.CreateVersion7(), Approver.Id, "Fix boiler", 5_000m, 10_000m, Now);
        request.Edit(OtherApprover.Id, "Replace boiler", 15_000m, "scope grew", Now);

        Assert.NotEqual(Approver.Id, request.PendingRevision!.SubmittedById);
        Assert.False(MaintenanceRequestAuthorizationHandler.CanDecide(As(Approver, ApproverRole), request));
    }

    [Fact]
    public void Requester_can_modify_only_their_own_requests_while_manage_permission_covers_any()
    {
        var request = PendingRequestBy(Requester);

        Assert.True(MaintenanceRequestAuthorizationHandler.CanModify(As(Requester, RequesterRole), request));
        Assert.False(MaintenanceRequestAuthorizationHandler.CanModify(As(OtherRequester, RequesterRole), request));
        Assert.True(MaintenanceRequestAuthorizationHandler.CanModify(As(Approver, ApproverRole), request));
    }

    [Fact]
    public void A_user_of_another_organization_is_refused_even_with_the_right_permissions()
    {
        var otherOrg = Guid.CreateVersion7();
        var foreignRole = Role.Create(otherOrg, "Approver", [Permissions.RequestsApprove, Permissions.RequestsManage]);
        var foreigner = User.Create(otherOrg, foreignRole.Id, "x@other.test", "Foreign Approver", "hash");
        var request = PendingRequestBy(Requester);

        Assert.False(MaintenanceRequestAuthorizationHandler.CanDecide(As(foreigner, foreignRole), request));
        Assert.False(MaintenanceRequestAuthorizationHandler.CanModify(As(foreigner, foreignRole), request));
    }
}
