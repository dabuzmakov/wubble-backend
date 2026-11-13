using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Wubble.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMonitoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Monitors",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    EmailNotificationsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Monitors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Monitors_AspNetUsers_OwnerId",
                        column: x => x.OwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Checks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MonitorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    Method = table.Column<string>(type: "character varying(4)", maxLength: 4, nullable: false),
                    IntervalSeconds = table.Column<int>(type: "integer", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    ExpectedStatusCode = table.Column<int>(type: "integer", nullable: false),
                    BodyContains = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    FailureThreshold = table.Column<int>(type: "integer", nullable: false),
                    RecoveryThreshold = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastOutcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    ConsecutiveSuccesses = table.Column<int>(type: "integer", nullable: false),
                    RunLeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Checks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Checks_Monitors_MonitorId",
                        column: x => x.MonitorId,
                        principalTable: "Monitors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "IncidentNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MonitorId = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsRecovery = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IncidentNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_IncidentNotifications_Monitors_MonitorId",
                        column: x => x.MonitorId,
                        principalTable: "Monitors",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CheckResults",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckId = table.Column<Guid>(type: "uuid", nullable: false),
                    Trigger = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DurationMs = table.Column<double>(type: "double precision", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CheckResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CheckResults_Checks_CheckId",
                        column: x => x.CheckId,
                        principalTable: "Checks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Incidents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpenedByResultId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClosedByResultId = table.Column<Guid>(type: "uuid", nullable: true),
                    OpenedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Incidents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Incidents_Checks_CheckId",
                        column: x => x.CheckId,
                        principalTable: "Checks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CheckResults_CheckId_StartedAt",
                table: "CheckResults",
                columns: new[] { "CheckId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Checks_IsEnabled_NextRunAt",
                table: "Checks",
                columns: new[] { "IsEnabled", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Checks_MonitorId",
                table: "Checks",
                column: "MonitorId");

            migrationBuilder.CreateIndex(
                name: "IX_IncidentNotifications_IncidentId_IsRecovery",
                table: "IncidentNotifications",
                columns: new[] { "IncidentId", "IsRecovery" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IncidentNotifications_MonitorId",
                table: "IncidentNotifications",
                column: "MonitorId");

            migrationBuilder.CreateIndex(
                name: "IX_IncidentNotifications_SentAt_NextAttemptAt",
                table: "IncidentNotifications",
                columns: new[] { "SentAt", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_CheckId",
                table: "Incidents",
                column: "CheckId",
                unique: true,
                filter: "\"ClosedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Incidents_CheckId_OpenedAt",
                table: "Incidents",
                columns: new[] { "CheckId", "OpenedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Monitors_OwnerId",
                table: "Monitors",
                column: "OwnerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CheckResults");

            migrationBuilder.DropTable(
                name: "IncidentNotifications");

            migrationBuilder.DropTable(
                name: "Incidents");

            migrationBuilder.DropTable(
                name: "Checks");

            migrationBuilder.DropTable(
                name: "Monitors");
        }
    }
}
