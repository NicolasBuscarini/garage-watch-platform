using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageWatch.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddPublicIds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PublicId",
                table: "UserAccessProfileAssignments",
                type: "TEXT",
                maxLength: 15,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicId",
                table: "ClipAnalysisJobs",
                type: "TEXT",
                maxLength: 15,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicId",
                table: "CameraContexts",
                type: "TEXT",
                maxLength: 15,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicId",
                table: "CameraCatalog",
                type: "TEXT",
                maxLength: 15,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicId",
                table: "AspNetUsers",
                type: "TEXT",
                maxLength: 15,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicId",
                table: "AccessRequests",
                type: "TEXT",
                maxLength: 15,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicId",
                table: "AccessProfileVersions",
                type: "TEXT",
                maxLength: 15,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicId",
                table: "AccessProfiles",
                type: "TEXT",
                maxLength: 15,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserAccessProfileAssignments_PublicId",
                table: "UserAccessProfileAssignments",
                column: "PublicId",
                unique: true,
                filter: "\"PublicId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ClipAnalysisJobs_PublicId",
                table: "ClipAnalysisJobs",
                column: "PublicId",
                unique: true,
                filter: "\"PublicId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CameraContexts_PublicId",
                table: "CameraContexts",
                column: "PublicId",
                unique: true,
                filter: "\"PublicId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CameraCatalog_PublicId",
                table: "CameraCatalog",
                column: "PublicId",
                unique: true,
                filter: "\"PublicId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_PublicId",
                table: "AspNetUsers",
                column: "PublicId",
                unique: true,
                filter: "\"PublicId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AccessRequests_PublicId",
                table: "AccessRequests",
                column: "PublicId",
                unique: true,
                filter: "\"PublicId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AccessProfileVersions_PublicId",
                table: "AccessProfileVersions",
                column: "PublicId",
                unique: true,
                filter: "\"PublicId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AccessProfiles_PublicId",
                table: "AccessProfiles",
                column: "PublicId",
                unique: true,
                filter: "\"PublicId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserAccessProfileAssignments_PublicId",
                table: "UserAccessProfileAssignments");

            migrationBuilder.DropIndex(
                name: "IX_ClipAnalysisJobs_PublicId",
                table: "ClipAnalysisJobs");

            migrationBuilder.DropIndex(
                name: "IX_CameraContexts_PublicId",
                table: "CameraContexts");

            migrationBuilder.DropIndex(
                name: "IX_CameraCatalog_PublicId",
                table: "CameraCatalog");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_PublicId",
                table: "AspNetUsers");

            migrationBuilder.DropIndex(
                name: "IX_AccessRequests_PublicId",
                table: "AccessRequests");

            migrationBuilder.DropIndex(
                name: "IX_AccessProfileVersions_PublicId",
                table: "AccessProfileVersions");

            migrationBuilder.DropIndex(
                name: "IX_AccessProfiles_PublicId",
                table: "AccessProfiles");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "UserAccessProfileAssignments");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "ClipAnalysisJobs");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "CameraContexts");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "CameraCatalog");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "AccessRequests");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "AccessProfileVersions");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "AccessProfiles");
        }
    }
}
