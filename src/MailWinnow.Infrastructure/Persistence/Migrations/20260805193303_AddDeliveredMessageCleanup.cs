using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailWinnow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveredMessageCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedUtc",
                table: "MessageDeliveries",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletionStartedUtc",
                table: "MessageDeliveries",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DestinationFolder",
                table: "MessageDeliveries",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DestinationMailboxId",
                table: "MessageDeliveries",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageDeliveries_State_ExpiresUtc",
                table: "MessageDeliveries",
                columns: new[] { "State", "ExpiresUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MessageDeliveries_State_ExpiresUtc",
                table: "MessageDeliveries");

            migrationBuilder.DropColumn(
                name: "DeletedUtc",
                table: "MessageDeliveries");

            migrationBuilder.DropColumn(
                name: "DeletionStartedUtc",
                table: "MessageDeliveries");

            migrationBuilder.DropColumn(
                name: "DestinationFolder",
                table: "MessageDeliveries");

            migrationBuilder.DropColumn(
                name: "DestinationMailboxId",
                table: "MessageDeliveries");
        }
    }
}
