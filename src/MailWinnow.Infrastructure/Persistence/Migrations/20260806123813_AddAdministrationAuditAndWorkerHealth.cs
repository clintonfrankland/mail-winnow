using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailWinnow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAdministrationAuditAndWorkerHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OccurredUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EventType = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    ActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    SubjectUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    ResourceType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ResourceId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Detail = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WorkerHeartbeats",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkerHeartbeats", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_OccurredUtc",
                table: "AuditEvents",
                column: "OccurredUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "WorkerHeartbeats");
        }
    }
}
