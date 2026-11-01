using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageWatch.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddFaceExtractorRegistry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "FaceExtractorCandidates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    PublicId = table.Column<string>(type: "TEXT", maxLength: 15, nullable: true),
                    ModelId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    ModelVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ArtifactSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SourceUri = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    CodeLicense = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    WeightsLicense = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    AdapterId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    AdapterContractVersion = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FaceExtractorCandidates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FaceExtractorSelections",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CandidateId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Version = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FaceExtractorSelections", x => x.Key);
                    table.ForeignKey(
                        name: "FK_FaceExtractorSelections_FaceExtractorCandidates_CandidateId",
                        column: x => x.CandidateId,
                        principalTable: "FaceExtractorCandidates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FaceExtractorCandidates_ModelId_ModelVersion_ArtifactSha256",
                table: "FaceExtractorCandidates",
                columns: new[] { "ModelId", "ModelVersion", "ArtifactSha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FaceExtractorCandidates_PublicId",
                table: "FaceExtractorCandidates",
                column: "PublicId",
                unique: true,
                filter: "\"PublicId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_FaceExtractorSelections_CandidateId",
                table: "FaceExtractorSelections",
                column: "CandidateId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FaceExtractorSelections");

            migrationBuilder.DropTable(
                name: "FaceExtractorCandidates");
        }
    }
}
