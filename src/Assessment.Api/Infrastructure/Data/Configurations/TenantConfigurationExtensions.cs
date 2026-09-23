using Assessment.Api.Domain;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assessment.Api.Infrastructure.Data.Configurations;

internal static class TenantConfigurationExtensions
{
    /// <summary>
    /// FK to organizations plus a unique (id, organization_id) key, which is the target for composite FKs
    /// from child tables. A child row can then only reference a parent in its own organization.
    /// </summary>
    public static void IsTenantOwned<T>(this EntityTypeBuilder<T> builder) where T : Entity, ITenantOwned
    {
        builder.HasOne<Organization>().WithMany().HasForeignKey(e => e.OrganizationId);
        builder.HasAlternateKey(e => new { e.Id, e.OrganizationId });
    }

    public static PropertyBuilder<decimal> IsMoney(this PropertyBuilder<decimal> property) =>
        property.HasPrecision(12, 2);

    public static PropertyBuilder<decimal?> IsMoney(this PropertyBuilder<decimal?> property) =>
        property.HasPrecision(12, 2);
}
