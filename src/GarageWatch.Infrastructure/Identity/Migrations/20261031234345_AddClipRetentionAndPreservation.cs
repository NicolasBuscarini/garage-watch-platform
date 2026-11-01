using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageWatch.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddClipRetentionAndPreservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClipPreservations",
                columns: table => new
                {
                    ClipId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PublicId = table.Column<string>(type: "TEXT", maxLength: 15, nullable: false),
                    CameraId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IsPreserved = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClipPreservations", x => x.ClipId);
                    table.ForeignKey(
                        name: "FK_ClipPreservations_CameraCatalog_CameraId",
                        column: x => x.CameraId,
                        principalTable: "CameraCatalog",
                        principalColumn: "CameraId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ClipRetentionPolicies",
                columns: table => new
                {
                    CameraId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RetentionDays = table.Column<int>(type: "INTEGER", nullable: false),
                    MaximumStorageBytes = table.Column<long>(type: "INTEGER", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClipRetentionPolicies", x => x.CameraId);
                    table.ForeignKey(
                        name: "FK_ClipRetentionPolicies_CameraCatalog_CameraId",
                        column: x => x.CameraId,
                        principalTable: "CameraCatalog",
                        principalColumn: "CameraId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClipPreservations_CameraId_IsPreserved",
                table: "ClipPreservations",
                columns: new[] { "CameraId", "IsPreserved" });

            migrationBuilder.CreateIndex(
                name: "IX_ClipPreservations_PublicId",
                table: "ClipPreservations",
                column: "PublicId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClipPreservations");

            migrationBuilder.DropTable(
                name: "ClipRetentionPolicies");
        }
    }
}
