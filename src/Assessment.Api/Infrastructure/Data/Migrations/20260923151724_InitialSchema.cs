using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Assessment.Api.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    approval_threshold = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false, defaultValue: 10000m),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_organizations", x => x.id);
                    table.CheckConstraint("ck_organizations_threshold", "approval_threshold >= 0");
                });

            migrationBuilder.CreateTable(
                name: "roles",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    is_system_admin = table.Column<bool>(type: "boolean", nullable: false),
                    permissions = table.Column<string[]>(type: "text[]", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_roles", x => x.id);
                    table.UniqueConstraint("ak_roles_id_organization_id", x => new { x.id, x.organization_id });
                    table.ForeignKey(
                        name: "fk_roles_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sites",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sites", x => x.id);
                    table.UniqueConstraint("ak_sites_id_organization_id", x => new { x.id, x.organization_id });
                    table.ForeignKey(
                        name: "fk_sites_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_id = table.Column<Guid>(type: "uuid", nullable: false),
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.UniqueConstraint("ak_users_id_organization_id", x => new { x.id, x.organization_id });
                    table.CheckConstraint("ck_users_email_lowercase", "email = lower(email)");
                    table.ForeignKey(
                        name: "fk_users_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_users_roles_role_id_organization_id",
                        columns: x => new { x.role_id, x.organization_id },
                        principalTable: "roles",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    entity_type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    from_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    to_status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    actor_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    details = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_audit_events_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_audit_events_users_actor_user_id_organization_id",
                        columns: x => new { x.actor_user_id, x.organization_id },
                        principalTable: "users",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "maintenance_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    site_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    approval_threshold = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    estimated_cost = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    actual_cost = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_maintenance_requests", x => x.id);
                    table.UniqueConstraint("ak_maintenance_requests_id_organization_id", x => new { x.id, x.organization_id });
                    table.CheckConstraint("ck_requests_actual_cost", "actual_cost IS NULL OR actual_cost > 0");
                    table.CheckConstraint("ck_requests_completed_has_actual", "status <> 'Completed' OR (actual_cost IS NOT NULL AND completed_at IS NOT NULL)");
                    table.CheckConstraint("ck_requests_estimated_cost", "estimated_cost > 0");
                    table.CheckConstraint("ck_requests_status", "status IN ('Raised', 'PendingApproval', 'Approved', 'Rejected', 'Completed')");
                    table.CheckConstraint("ck_requests_threshold", "approval_threshold >= 0");
                    table.ForeignKey(
                        name: "fk_maintenance_requests_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_maintenance_requests_sites_site_id_organization_id",
                        columns: x => new { x.site_id, x.organization_id },
                        principalTable: "sites",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_maintenance_requests_users_requested_by_id_organization_id",
                        columns: x => new { x.requested_by_id, x.organization_id },
                        principalTable: "users",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "request_revisions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    organization_id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    amount = table.Column<decimal>(type: "numeric(12,2)", precision: 12, scale: 2, nullable: false),
                    previous_revision_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    submitted_by_id = table.Column<Guid>(type: "uuid", nullable: false),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    decided_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decision_comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by_id = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_by_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_request_revisions", x => x.id);
                    table.UniqueConstraint("ak_request_revisions_id_organization_id", x => new { x.id, x.organization_id });
                    table.CheckConstraint("ck_revisions_amount", "amount > 0");
                    table.CheckConstraint("ck_revisions_kind", "kind IN ('Initial', 'Edit', 'ActualCost')");
                    table.CheckConstraint("ck_revisions_not_self_decided", "decided_by_id IS NULL OR decided_by_id <> submitted_by_id");
                    table.CheckConstraint("ck_revisions_outcome", "outcome IN ('Pending', 'AutoApproved', 'Approved', 'Rejected', 'Superseded')");
                    table.ForeignKey(
                        name: "fk_request_revisions_maintenance_requests_request_id_organizat",
                        columns: x => new { x.request_id, x.organization_id },
                        principalTable: "maintenance_requests",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_request_revisions_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_request_revisions_request_revisions_previous_revision_id_or",
                        columns: x => new { x.previous_revision_id, x.organization_id },
                        principalTable: "request_revisions",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_request_revisions_users_decided_by_id_organization_id",
                        columns: x => new { x.decided_by_id, x.organization_id },
                        principalTable: "users",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_request_revisions_users_submitted_by_id_organization_id",
                        columns: x => new { x.submitted_by_id, x.organization_id },
                        principalTable: "users",
                        principalColumns: new[] { "id", "organization_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_actor_user_id_organization_id",
                table: "audit_events",
                columns: new[] { "actor_user_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_organization_id_entity_id_created_at",
                table: "audit_events",
                columns: new[] { "organization_id", "entity_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_requests_organization_id_status_created_at",
                table: "maintenance_requests",
                columns: new[] { "organization_id", "status", "created_at" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_requests_requested_by_id_organization_id",
                table: "maintenance_requests",
                columns: new[] { "requested_by_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_requests_site_id_organization_id",
                table: "maintenance_requests",
                columns: new[] { "site_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_requests_spend_report",
                table: "maintenance_requests",
                columns: new[] { "organization_id", "site_id", "completed_at" },
                filter: "status = 'Completed'")
                .Annotation("Npgsql:IndexInclude", new[] { "actual_cost" });

            migrationBuilder.CreateIndex(
                name: "ix_request_revisions_decided_by_id_organization_id",
                table: "request_revisions",
                columns: new[] { "decided_by_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_request_revisions_organization_id",
                table: "request_revisions",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_request_revisions_previous_revision_id_organization_id",
                table: "request_revisions",
                columns: new[] { "previous_revision_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_request_revisions_request_id_organization_id",
                table: "request_revisions",
                columns: new[] { "request_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ix_request_revisions_request_id_sequence",
                table: "request_revisions",
                columns: new[] { "request_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_request_revisions_submitted_by_id_organization_id",
                table: "request_revisions",
                columns: new[] { "submitted_by_id", "organization_id" });

            migrationBuilder.CreateIndex(
                name: "ux_request_revisions_one_pending",
                table: "request_revisions",
                column: "request_id",
                unique: true,
                filter: "outcome = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_roles_organization_id_name",
                table: "roles",
                columns: new[] { "organization_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sites_organization_id_name",
                table: "sites",
                columns: new[] { "organization_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_email",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_organization_id",
                table: "users",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "ix_users_role_id_organization_id",
                table: "users",
                columns: new[] { "role_id", "organization_id" });

            // Audit trail is append-only at the database level, not just by convention in the app:
            // even the application's own DB user cannot rewrite or erase history.
            migrationBuilder.Sql("""
                CREATE FUNCTION audit_events_block_changes() RETURNS trigger
                LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'audit_events is append-only: % is not allowed', TG_OP
                        USING ERRCODE = 'insufficient_privilege';
                END;
                $$;

                CREATE TRIGGER audit_events_no_update_delete
                    BEFORE UPDATE OR DELETE ON audit_events
                    FOR EACH ROW EXECUTE FUNCTION audit_events_block_changes();

                CREATE TRIGGER audit_events_no_truncate
                    BEFORE TRUNCATE ON audit_events
                    FOR EACH STATEMENT EXECUTE FUNCTION audit_events_block_changes();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS audit_events_no_truncate ON audit_events;
                DROP TRIGGER IF EXISTS audit_events_no_update_delete ON audit_events;
                DROP FUNCTION IF EXISTS audit_events_block_changes();
                """);

            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "request_revisions");

            migrationBuilder.DropTable(
                name: "maintenance_requests");

            migrationBuilder.DropTable(
                name: "sites");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "roles");

            migrationBuilder.DropTable(
                name: "organizations");
        }
    }
}
