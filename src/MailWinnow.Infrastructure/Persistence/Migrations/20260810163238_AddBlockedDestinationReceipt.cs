using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailWinnow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBlockedDestinationReceipt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "BlockedDestinationMailboxId",
                table: "SourceMessageHeaders",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BlockedDestinationUid",
                table: "SourceMessageHeaders",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BlockedDestinationUidValidity",
                table: "SourceMessageHeaders",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BlockedDestinationMailboxId",
                table: "SourceMessageHeaders");

            migrationBuilder.DropColumn(
                name: "BlockedDestinationUid",
                table: "SourceMessageHeaders");

            migrationBuilder.DropColumn(
                name: "BlockedDestinationUidValidity",
                table: "SourceMessageHeaders");
        }
    }
}
