using Assessment.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assessment.Api.Infrastructure.Data.Configurations;

internal sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> builder)
    {
        builder.ToTable("organizations", t => t.HasCheckConstraint("ck_organizations_threshold", "approval_threshold >= 0"));
        builder.Property(o => o.Name).HasMaxLength(200);
        builder.Property(o => o.ApprovalThreshold).IsMoney().HasDefaultValue(Organization.DefaultApprovalThreshold);
    }
}

internal sealed class SiteConfiguration : IEntityTypeConfiguration<Site>
{
    public void Configure(EntityTypeBuilder<Site> builder)
    {
        builder.ToTable("sites");
        builder.IsTenantOwned();
        builder.Property(s => s.Name).HasMaxLength(200);
        builder.HasIndex(s => new { s.OrganizationId, s.Name }).IsUnique();
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("roles");
        builder.IsTenantOwned();
        builder.Property(r => r.Name).HasMaxLength(100);
        builder.HasIndex(r => new { r.OrganizationId, r.Name }).IsUnique();

        // text[]: permissions are only ever read per role, never queried across roles.
        builder.PrimitiveCollection(r => r.Permissions)
            .HasField("_permissions")
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users", t => t.HasCheckConstraint("ck_users_email_lowercase", "email = lower(email)"));
        builder.IsTenantOwned();
        builder.Property(u => u.Email).HasMaxLength(254);
        builder.Property(u => u.DisplayName).HasMaxLength(200);
        builder.Property(u => u.PasswordHash).HasMaxLength(500);

        // Stored lower-cased (CHECK above), so a plain unique index is case-insensitive in effect.
        builder.HasIndex(u => u.Email).IsUnique();

        builder.HasOne(u => u.Role).WithMany()
            .HasForeignKey(u => new { u.RoleId, u.OrganizationId })
            .HasPrincipalKey(r => new { r.Id, r.OrganizationId });
    }
}
