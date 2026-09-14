using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailWinnow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNavigationCountSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "InboxCountObservedUtc",
                table: "DestinationMailboxes",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InboxMessageCount",
                table: "DestinationMailboxes",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InboxCountObservedUtc",
                table: "DestinationMailboxes");

            migrationBuilder.DropColumn(
                name: "InboxMessageCount",
                table: "DestinationMailboxes");
        }
    }
}
