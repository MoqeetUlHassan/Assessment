using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Assessment.Api.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RequestListIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_maintenance_requests_organization_id_status_created_at",
                table: "maintenance_requests");

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_requests_list",
                table: "maintenance_requests",
                columns: new[] { "organization_id", "created_at", "id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_requests_list_by_status",
                table: "maintenance_requests",
                columns: new[] { "organization_id", "status", "created_at", "id" },
                descending: new[] { false, false, true, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_maintenance_requests_list",
                table: "maintenance_requests");

            migrationBuilder.DropIndex(
                name: "ix_maintenance_requests_list_by_status",
                table: "maintenance_requests");

            migrationBuilder.CreateIndex(
                name: "ix_maintenance_requests_organization_id_status_created_at",
                table: "maintenance_requests",
                columns: new[] { "organization_id", "status", "created_at" },
                descending: new[] { false, false, true });
        }
    }
}
