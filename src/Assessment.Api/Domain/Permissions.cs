namespace Assessment.Api.Domain;

/// <summary>
/// Fixed permission catalog. Authorization checks these names, never role names.
/// </summary>
public static class Permissions
{
    public const string RequestsCreate = "requests.create";   // raise; edit/complete own
    public const string RequestsManage = "requests.manage";   // edit/complete any in org
    public const string RequestsApprove = "requests.approve"; // decide pending revisions (never own)
    public const string ReportsSpend = "reports.spend";
    public const string AdminUsers = "admin.users";
    public const string AdminRoles = "admin.roles";
    public const string AdminSettings = "admin.settings";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        RequestsCreate, RequestsManage, RequestsApprove, ReportsSpend, AdminUsers, AdminRoles, AdminSettings,
    };

    /// <summary>Held only by the locked OrgAdmin role; cannot be granted to any other role.</summary>
    public static readonly IReadOnlySet<string> AdminOnly = new HashSet<string>
    {
        AdminUsers, AdminRoles, AdminSettings,
    };
}
