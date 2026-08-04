using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailWinnow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddHeaderSynchronization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSyncAttemptUtc",
                table: "SourceMailboxes",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LastSyncHeaderCount",
                table: "SourceMailboxes",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastSyncSucceededUtc",
                table: "SourceMailboxes",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SyncRequestedUtc",
                table: "SourceMailboxes",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SourceMailboxFolderSyncStates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceMailboxId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FolderName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    UidValidity = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceMailboxFolderSyncStates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourceMessageHeaders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceMailboxId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FolderName = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    UidValidity = table.Column<long>(type: "bigint", nullable: false),
                    Uid = table.Column<long>(type: "bigint", nullable: false),
                    MessageId = table.Column<string>(type: "nvarchar(998)", maxLength: 998, nullable: true),
                    Date = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    From = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    To = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Subject = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ReceivedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceMessageHeaders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SourceMailboxFolderSyncStates_SourceMailboxId_FolderName",
                table: "SourceMailboxFolderSyncStates",
                columns: new[] { "SourceMailboxId", "FolderName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceMessageHeaders_SourceMailboxId_FolderName_UidValidity_Uid",
                table: "SourceMessageHeaders",
                columns: new[] { "SourceMailboxId", "FolderName", "UidValidity", "Uid" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SourceMailboxFolderSyncStates");

            migrationBuilder.DropTable(
                name: "SourceMessageHeaders");

            migrationBuilder.DropColumn(
                name: "LastSyncAttemptUtc",
                table: "SourceMailboxes");

            migrationBuilder.DropColumn(
                name: "LastSyncHeaderCount",
                table: "SourceMailboxes");

            migrationBuilder.DropColumn(
                name: "LastSyncSucceededUtc",
                table: "SourceMailboxes");

            migrationBuilder.DropColumn(
                name: "SyncRequestedUtc",
                table: "SourceMailboxes");
        }
    }
}
