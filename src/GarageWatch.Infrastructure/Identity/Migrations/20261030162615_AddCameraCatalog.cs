using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageWatch.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddCameraCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CameraContexts",
                columns: table => new
                {
                    ContextId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CameraContexts", x => x.ContextId);
                });

            migrationBuilder.CreateTable(
                name: "CameraCatalog",
                columns: table => new
                {
                    CameraId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ContextId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CameraCatalog", x => x.CameraId);
                    table.ForeignKey(
                        name: "FK_CameraCatalog_CameraContexts_ContextId",
                        column: x => x.ContextId,
                        principalTable: "CameraContexts",
                        principalColumn: "ContextId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CameraCatalog_ContextId_Name",
                table: "CameraCatalog",
                columns: new[] { "ContextId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_CameraContexts_Name",
                table: "CameraContexts",
                column: "Name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CameraCatalog");

            migrationBuilder.DropTable(
                name: "CameraContexts");
        }
    }
}
