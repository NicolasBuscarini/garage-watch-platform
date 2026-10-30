using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageWatch.Infrastructure.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddIamAuditActorRelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_IamAuditEvents_ActorUserId",
                table: "IamAuditEvents",
                column: "ActorUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_IamAuditEvents_AspNetUsers_ActorUserId",
                table: "IamAuditEvents",
                column: "ActorUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_IamAuditEvents_AspNetUsers_ActorUserId",
                table: "IamAuditEvents");

            migrationBuilder.DropIndex(
                name: "IX_IamAuditEvents_ActorUserId",
                table: "IamAuditEvents");
        }
    }
}
