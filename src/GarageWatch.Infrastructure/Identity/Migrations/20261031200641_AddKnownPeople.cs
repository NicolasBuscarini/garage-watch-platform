using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageWatch.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddKnownPeople : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "KnownPeople",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PublicId = table.Column<string>(type: "TEXT", maxLength: 15, nullable: true),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Purpose = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AuthorizationRecordedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    DeletedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KnownPeople", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KnownPeople_DeletedAtUtc_DisplayName",
                table: "KnownPeople",
                columns: new[] { "DeletedAtUtc", "DisplayName" });

            migrationBuilder.CreateIndex(
                name: "IX_KnownPeople_PublicId",
                table: "KnownPeople",
                column: "PublicId",
                unique: true,
                filter: "\"PublicId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KnownPeople");
        }
    }
}
