using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageWatch.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddPerCameraClipTriggerConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ClipTriggerEnabled",
                table: "CameraCatalog",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "ClipTriggerVersion",
                table: "CameraCatalog",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClipTriggerEnabled",
                table: "CameraCatalog");

            migrationBuilder.DropColumn(
                name: "ClipTriggerVersion",
                table: "CameraCatalog");
        }
    }
}
