using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailWinnow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRuleEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EvaluatedUtc",
                table: "SourceMessageHeaders",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "EvaluationOutcome",
                table: "SourceMessageHeaders",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "MailRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Action = table.Column<int>(type: "int", nullable: false),
                    Scope = table.Column<int>(type: "int", nullable: false),
                    MatchType = table.Column<int>(type: "int", nullable: false),
                    MatchValue = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    SourceMailboxId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EffectiveUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ExpiresUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    DeliveredMessageRetentionDays = table.Column<int>(type: "int", nullable: true),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MailRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MessageDecisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    SourceMessageHeaderId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Action = table.Column<int>(type: "int", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageDecisions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MailRules_OwnerUserId_Scope_SourceMailboxId",
                table: "MailRules",
                columns: new[] { "OwnerUserId", "Scope", "SourceMailboxId" });

            migrationBuilder.CreateIndex(
                name: "IX_MessageDecisions_OwnerUserId_SourceMessageHeaderId",
                table: "MessageDecisions",
                columns: new[] { "OwnerUserId", "SourceMessageHeaderId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MailRules");

            migrationBuilder.DropTable(
                name: "MessageDecisions");

            migrationBuilder.DropColumn(
                name: "EvaluatedUtc",
                table: "SourceMessageHeaders");

            migrationBuilder.DropColumn(
                name: "EvaluationOutcome",
                table: "SourceMessageHeaders");
        }
    }
}
