using Assessment.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Assessment.Api.Infrastructure.Data.Configurations;

internal sealed class MaintenanceRequestConfiguration : IEntityTypeConfiguration<MaintenanceRequest>
{
    public void Configure(EntityTypeBuilder<MaintenanceRequest> builder)
    {
        builder.ToTable("maintenance_requests", t =>
        {
            t.HasCheckConstraint("ck_requests_estimated_cost", "estimated_cost > 0");
            t.HasCheckConstraint("ck_requests_actual_cost", "actual_cost IS NULL OR actual_cost > 0");
            t.HasCheckConstraint("ck_requests_threshold", "approval_threshold >= 0");
            t.HasCheckConstraint("ck_requests_status",
                "status IN ('Raised', 'PendingApproval', 'Approved', 'Rejected', 'Completed')");
            t.HasCheckConstraint("ck_requests_completed_has_actual",
                "status <> 'Completed' OR (actual_cost IS NOT NULL AND completed_at IS NOT NULL)");
        });
        builder.IsTenantOwned();

        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.Description).HasMaxLength(2000);
        builder.Property(r => r.EstimatedCost).IsMoney();
        builder.Property(r => r.ActualCost).IsMoney();
        builder.Property(r => r.ApprovalThreshold).IsMoney();

        // Optimistic concurrency on Postgres' xmin: two approvers deciding at once → one gets 409.
        builder.Property<uint>("Version").IsRowVersion();

        builder.HasOne<Site>().WithMany()
            .HasForeignKey(r => new { r.SiteId, r.OrganizationId })
            .HasPrincipalKey(s => new { s.Id, s.OrganizationId });
        builder.HasOne<User>().WithMany()
            .HasForeignKey(r => new { r.RequestedById, r.OrganizationId })
            .HasPrincipalKey(u => new { u.Id, u.OrganizationId });

        builder.HasMany(r => r.Revisions).WithOne()
            .HasForeignKey(v => new { v.RequestId, v.OrganizationId })
            .HasPrincipalKey(r => new { r.Id, r.OrganizationId });

        // The aggregate's rules (pending revision, last approved content) need its revisions.
        // Auto-include so no query can load a request without them and silently get wrong answers.
        builder.Navigation(r => r.Revisions).HasField("_revisions").AutoInclude();

        builder.Ignore(r => r.PendingRevision);
        builder.Ignore(r => r.LastApprovedContent);
        builder.Ignore(r => r.AllowedActions);

        // List + approval queue.
        builder.HasIndex(r => new { r.OrganizationId, r.Status, r.CreatedAt })
            .IsDescending(false, false, true);

        // Spend report: covering, and only over completed requests.
        builder.HasIndex(r => new { r.OrganizationId, r.SiteId, r.CompletedAt })
            .HasDatabaseName("ix_maintenance_requests_spend_report")
            .HasFilter("status = 'Completed'")
            .IncludeProperties(r => r.ActualCost);
    }
}

internal sealed class RequestRevisionConfiguration : IEntityTypeConfiguration<RequestRevision>
{
    public void Configure(EntityTypeBuilder<RequestRevision> builder)
    {
        builder.ToTable("request_revisions", t =>
        {
            t.HasCheckConstraint("ck_revisions_amount", "amount > 0");
            t.HasCheckConstraint("ck_revisions_kind", "kind IN ('Initial', 'Edit', 'ActualCost')");
            t.HasCheckConstraint("ck_revisions_outcome",
                "outcome IN ('Pending', 'AutoApproved', 'Approved', 'Rejected', 'Superseded')");
            // Backstop for the authorization rule: nobody decides a revision they submitted.
            t.HasCheckConstraint("ck_revisions_not_self_decided",
                "decided_by_id IS NULL OR decided_by_id <> submitted_by_id");
        });
        builder.IsTenantOwned();

        builder.Property(v => v.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(v => v.Outcome).HasConversion<string>().HasMaxLength(20);
        builder.Property(v => v.Description).HasMaxLength(2000);
        builder.Property(v => v.Amount).IsMoney();
        builder.Property(v => v.Reason).HasMaxLength(1000);
        builder.Property(v => v.DecisionComment).HasMaxLength(1000);
        builder.Ignore(v => v.IsApprovedContent);

        builder.HasOne<User>().WithMany()
            .HasForeignKey(v => new { v.SubmittedById, v.OrganizationId })
            .HasPrincipalKey(u => new { u.Id, u.OrganizationId });
        builder.HasOne<User>().WithMany()
            .HasForeignKey(v => new { v.DecidedById, v.OrganizationId })
            .HasPrincipalKey(u => new { u.Id, u.OrganizationId });

        builder.HasOne<RequestRevision>().WithMany()
            .HasForeignKey(v => new { v.PreviousRevisionId, v.OrganizationId })
            .HasPrincipalKey(v => new { v.Id, v.OrganizationId });

        builder.HasIndex(v => new { v.RequestId, v.Sequence }).IsUnique();

        // At most one revision awaiting a decision per request, enforced by the database.
        builder.HasIndex(v => v.RequestId)
            .HasDatabaseName("ux_request_revisions_one_pending")
            .IsUnique()
            .HasFilter("outcome = 'Pending'");
    }
}
