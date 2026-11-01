using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageWatch.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddBiometricEnrollments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BiometricEnrollments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PublicId = table.Column<string>(type: "TEXT", maxLength: 15, nullable: true),
                    UserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    EnrolledAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    RevokedAtUtc = table.Column<long>(type: "INTEGER", nullable: true),
                    RevokedReason = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    EmbeddingCiphertext = table.Column<byte[]>(type: "BLOB", nullable: false),
                    EmbeddingNonce = table.Column<byte[]>(type: "BLOB", nullable: false),
                    EmbeddingTag = table.Column<byte[]>(type: "BLOB", nullable: false),
                    KeyVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Dimensions = table.Column<int>(type: "INTEGER", nullable: false),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ModelVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SchemaVersion = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AdapterId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BiometricEnrollments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BiometricEnrollments_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BiometricEnrollments_PublicId",
                table: "BiometricEnrollments",
                column: "PublicId",
                unique: true,
                filter: "\"PublicId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BiometricEnrollments_Status_ExpiresAtUtc",
                table: "BiometricEnrollments",
                columns: new[] { "Status", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BiometricEnrollments_UserId",
                table: "BiometricEnrollments",
                column: "UserId",
                unique: true,
                filter: "\"Status\" = 'active'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BiometricEnrollments");
        }
    }
}
