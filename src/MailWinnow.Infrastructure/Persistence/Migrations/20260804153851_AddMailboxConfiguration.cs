using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailWinnow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMailboxConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DestinationMailboxes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Username = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    ProtectedCredential = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Folder = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DestinationMailboxes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SourceMailboxes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Host = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "int", nullable: false),
                    UseSsl = table.Column<bool>(type: "bit", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    ProtectedCredential = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    SelectedFoldersJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PollingStatus = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    LastSuccessfulConnectionUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    SanitizedError = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SourceMailboxes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DestinationMailboxes_OwnerUserId",
                table: "DestinationMailboxes",
                column: "OwnerUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceMailboxes_OwnerUserId_DisplayName",
                table: "SourceMailboxes",
                columns: new[] { "OwnerUserId", "DisplayName" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DestinationMailboxes");

            migrationBuilder.DropTable(
                name: "SourceMailboxes");
        }
    }
}
