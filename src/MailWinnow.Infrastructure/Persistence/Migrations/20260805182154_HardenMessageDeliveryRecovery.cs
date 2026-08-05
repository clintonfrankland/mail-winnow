using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailWinnow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenMessageDeliveryRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ApprovalRuleId",
                table: "MessageDeliveries",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DestinationAppendStartedUtc",
                table: "MessageDeliveries",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DestinationUidFloor",
                table: "MessageDeliveries",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveries_ApprovalRuleId",
                table: "MessageDeliveries",
                column: "ApprovalRuleId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MessageDeliveries_ApprovalRuleId",
                table: "MessageDeliveries");

            migrationBuilder.DropColumn(
                name: "ApprovalRuleId",
                table: "MessageDeliveries");

            migrationBuilder.DropColumn(
                name: "DestinationAppendStartedUtc",
                table: "MessageDeliveries");

            migrationBuilder.DropColumn(
                name: "DestinationUidFloor",
                table: "MessageDeliveries");
        }
    }
}
