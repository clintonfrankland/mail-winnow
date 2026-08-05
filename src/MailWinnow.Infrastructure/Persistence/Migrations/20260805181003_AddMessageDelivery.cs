using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailWinnow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MessageDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceMessageHeaderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    DestinationUid = table.Column<long>(type: "bigint", nullable: true),
                    DestinationUidValidity = table.Column<long>(type: "bigint", nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    FetchStartedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeliveryStartedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeliveredUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ExpiresUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RetryRequestedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RetryRequestedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    LastFailureStage = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    SanitizedError = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageDeliveries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveries_OwnerUserId_State",
                table: "MessageDeliveries",
                columns: new[] { "OwnerUserId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveries_SourceMessageHeaderId",
                table: "MessageDeliveries",
                column: "SourceMessageHeaderId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessageDeliveries");
        }
    }
}
