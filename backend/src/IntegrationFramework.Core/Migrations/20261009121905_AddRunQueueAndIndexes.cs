using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace IntegrationFramework.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddRunQueueAndIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_WorkflowId",
                table: "WorkflowRuns");

            migrationBuilder.CreateTable(
                name: "RunQueueItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    WorkflowId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InputJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TriggerType = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    DedupeKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    EnqueuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ClaimedBy = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ClaimedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RunId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    WebhookEventId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Error = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Version = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunQueueItems", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_StartedAt_Status",
                table: "WorkflowRuns",
                columns: new[] { "StartedAt", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_WorkflowId_StartedAt",
                table: "WorkflowRuns",
                columns: new[] { "WorkflowId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RunQueueItems_Status_EnqueuedAt",
                table: "RunQueueItems",
                columns: new[] { "Status", "EnqueuedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RunQueueItems_WorkflowId_DedupeKey",
                table: "RunQueueItems",
                columns: new[] { "WorkflowId", "DedupeKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RunQueueItems");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_StartedAt_Status",
                table: "WorkflowRuns");

            migrationBuilder.DropIndex(
                name: "IX_WorkflowRuns_WorkflowId_StartedAt",
                table: "WorkflowRuns");

            migrationBuilder.CreateIndex(
                name: "IX_WorkflowRuns_WorkflowId",
                table: "WorkflowRuns",
                column: "WorkflowId");
        }
    }
}
