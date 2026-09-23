using System.Text.Json;
using Assessment.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assessment.Api.Infrastructure.Data.Configurations;

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        // UPDATE/DELETE/TRUNCATE are blocked by a trigger created in the migration.
        builder.ToTable("audit_events");
        builder.Property(a => a.Id).UseIdentityAlwaysColumn();
        builder.Property(a => a.EntityType).HasMaxLength(50);
        builder.Property(a => a.Action).HasMaxLength(50);
        builder.Property(a => a.FromStatus).HasMaxLength(20);
        builder.Property(a => a.ToStatus).HasMaxLength(20);

        builder.Property(a => a.Details)
            .HasColumnType("jsonb")
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => JsonSerializer.Deserialize<Dictionary<string, object?>>(v, (JsonSerializerOptions?)null)!,
                // Rows are immutable, so reference equality is enough for change tracking.
                new ValueComparer<IReadOnlyDictionary<string, object?>>(
                    (a, b) => ReferenceEquals(a, b), v => v.GetHashCode(), v => v));

        builder.HasOne<Organization>().WithMany().HasForeignKey(a => a.OrganizationId);
        builder.HasOne<User>().WithMany()
            .HasForeignKey(a => new { a.ActorUserId, a.OrganizationId })
            .HasPrincipalKey(u => new { u.Id, u.OrganizationId });

        builder.HasIndex(a => new { a.OrganizationId, a.EntityId, a.CreatedAt });
    }
}
