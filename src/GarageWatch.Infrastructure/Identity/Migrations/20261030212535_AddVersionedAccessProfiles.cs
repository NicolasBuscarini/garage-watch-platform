using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageWatch.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddVersionedAccessProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccessProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IamMigrationStates",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    CompletedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IamMigrationStates", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "AccessProfileVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProfileId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessProfileVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccessProfileVersions_AccessProfiles_ProfileId",
                        column: x => x.ProfileId,
                        principalTable: "AccessProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AccessProfilePermissions",
                columns: table => new
                {
                    AccessProfileVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PermissionKey = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccessProfilePermissions", x => new { x.AccessProfileVersionId, x.PermissionKey });
                    table.ForeignKey(
                        name: "FK_AccessProfilePermissions_AccessProfileVersions_AccessProfileVersionId",
                        column: x => x.AccessProfileVersionId,
                        principalTable: "AccessProfileVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserAccessProfileAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccessProfileVersionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssignedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AssignedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    RevokedAtUtc = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAccessProfileAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserAccessProfileAssignments_AccessProfileVersions_AccessProfileVersionId",
                        column: x => x.AccessProfileVersionId,
                        principalTable: "AccessProfileVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserAccessProfileAssignments_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "UserAccessProfileScopes",
                columns: table => new
                {
                    AssignmentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ContextId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CameraId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAccessProfileScopes", x => new { x.AssignmentId, x.ContextId, x.CameraId });
                    table.ForeignKey(
                        name: "FK_UserAccessProfileScopes_UserAccessProfileAssignments_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "UserAccessProfileAssignments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AccessProfiles_Name",
                table: "AccessProfiles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccessProfileVersions_ProfileId_Version",
                table: "AccessProfileVersions",
                columns: new[] { "ProfileId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserAccessProfileAssignments_AccessProfileVersionId",
                table: "UserAccessProfileAssignments",
                column: "AccessProfileVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_UserAccessProfileAssignments_UserId_AccessProfileVersionId",
                table: "UserAccessProfileAssignments",
                columns: new[] { "UserId", "AccessProfileVersionId" },
                unique: true,
                filter: "\"RevokedAtUtc\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccessProfilePermissions");

            migrationBuilder.DropTable(
                name: "IamMigrationStates");

            migrationBuilder.DropTable(
                name: "UserAccessProfileScopes");

            migrationBuilder.DropTable(
                name: "UserAccessProfileAssignments");

            migrationBuilder.DropTable(
                name: "AccessProfileVersions");

            migrationBuilder.DropTable(
                name: "AccessProfiles");
        }
    }
}
