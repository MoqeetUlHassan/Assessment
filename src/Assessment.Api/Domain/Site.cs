namespace Assessment.Api.Domain;

public class Site : Entity, ITenantOwned
{
    public Guid OrganizationId { get; private init; }
    public string Name { get; private set; } = null!;

    private Site() { }

    public static Site Create(Guid organizationId, string name) =>
        new() { OrganizationId = organizationId, Name = name.Trim() };
}
