using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageWatch.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddClipAnalysisDispatch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClipAnalysisDispatchPolicies",
                columns: table => new
                {
                    CameraId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ContextId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Policy = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClipAnalysisDispatchPolicies", x => x.CameraId);
                    table.ForeignKey(
                        name: "FK_ClipAnalysisDispatchPolicies_CameraCatalog_CameraId",
                        column: x => x.CameraId,
                        principalTable: "CameraCatalog",
                        principalColumn: "CameraId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ClipAnalysisJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClipId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ContextId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CameraId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<Guid>(type: "TEXT", nullable: false),
                    AnalysisVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PolicyVersion = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    RequestKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LeaseId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LeaseExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClipAnalysisJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClipAnalysisJobs_ClipId_AnalysisVersion",
                table: "ClipAnalysisJobs",
                columns: new[] { "ClipId", "AnalysisVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClipAnalysisJobs_ClipId_IdempotencyKey",
                table: "ClipAnalysisJobs",
                columns: new[] { "ClipId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClipAnalysisJobs_State_CreatedAtUtc",
                table: "ClipAnalysisJobs",
                columns: new[] { "State", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClipAnalysisDispatchPolicies");

            migrationBuilder.DropTable(
                name: "ClipAnalysisJobs");
        }
    }
}
