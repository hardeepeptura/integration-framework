using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntegrationFramework.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddUsersRolesAndSharing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnerEmail",
                table: "Workflows",
                type: "nvarchar(450)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AppUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Role = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUsers", x => x.Id);
                    table.UniqueConstraint("AK_AppUsers_Email", x => x.Email);
                });

            migrationBuilder.CreateTable(
                name: "WorkflowShares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Permission = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkflowShares", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkflowShares_Workflows_WorkflowId",
                        column: x => x.WorkflowId,
                        principalTable: "Workflows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Workflows_OwnerEmail",
                table: "Workflows",
                column: "OwnerEmail");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowShares_WorkflowId_Email",
                table: "WorkflowShares",
                columns: new[] { "WorkflowId", "Email" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppUsers");

            migrationBuilder.DropTable(
                name: "WorkflowShares");

            migrationBuilder.DropIndex(
                name: "IX_Workflows_OwnerEmail",
                table: "Workflows");

            migrationBuilder.DropColumn(
                name: "OwnerEmail",
                table: "Workflows");
        }
    }
}
