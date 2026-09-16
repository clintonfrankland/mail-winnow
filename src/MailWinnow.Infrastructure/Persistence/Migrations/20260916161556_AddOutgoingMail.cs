using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailWinnow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOutgoingMail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MessageDrafts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    SendingAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ProtectedContent = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ProtectedSubject = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UpdatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    OutboxId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageDrafts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutgoingMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    DraftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DraftRevision = table.Column<long>(type: "bigint", nullable: false),
                    SendingAccountId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProtectedMime = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    ProtectedSettings = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ProtectedSubject = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    State = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    SentUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    NextAttemptUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseExpiresUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AttemptCount = table.Column<int>(type: "int", nullable: false),
                    FailureCode = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Retryable = table.Column<bool>(type: "bit", nullable: false),
                    SentCopyStatus = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutgoingMessages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SendingAccounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    SourceMailboxId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DisplayName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    FromAddress = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    Host = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    Port = table.Column<int>(type: "int", nullable: false),
                    UseStartTls = table.Column<bool>(type: "bit", nullable: false),
                    UseAuthentication = table.Column<bool>(type: "bit", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    ProtectedPassword = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Enabled = table.Column<bool>(type: "bit", nullable: false),
                    SentCopyPolicy = table.Column<int>(type: "int", nullable: false),
                    SentFolder = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SendingAccounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DraftAttachments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DraftId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(180)", maxLength: 180, nullable: false),
                    ContentType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Length = table.Column<int>(type: "int", nullable: false),
                    ProtectedContent = table.Column<byte[]>(type: "varbinary(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DraftAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DraftAttachments_MessageDrafts_DraftId",
                        column: x => x.DraftId,
                        principalTable: "MessageDrafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DraftAttachments_DraftId",
                table: "DraftAttachments",
                column: "DraftId");

            migrationBuilder.CreateIndex(
                name: "IX_DraftAttachments_OwnerUserId",
                table: "DraftAttachments",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageDrafts_OwnerUserId_UpdatedUtc",
                table: "MessageDrafts",
                columns: new[] { "OwnerUserId", "UpdatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OutgoingMessages_OwnerUserId_DraftId_DraftRevision",
                table: "OutgoingMessages",
                columns: new[] { "OwnerUserId", "DraftId", "DraftRevision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutgoingMessages_State_NextAttemptUtc_CreatedUtc",
                table: "OutgoingMessages",
                columns: new[] { "State", "NextAttemptUtc", "CreatedUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_OutgoingMessages_State_SentUtc",
                table: "OutgoingMessages",
                columns: new[] { "State", "SentUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SendingAccounts_OwnerUserId_SourceMailboxId",
                table: "SendingAccounts",
                columns: new[] { "OwnerUserId", "SourceMailboxId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DraftAttachments");

            migrationBuilder.DropTable(
                name: "OutgoingMessages");

            migrationBuilder.DropTable(
                name: "SendingAccounts");

            migrationBuilder.DropTable(
                name: "MessageDrafts");
        }
    }
}
