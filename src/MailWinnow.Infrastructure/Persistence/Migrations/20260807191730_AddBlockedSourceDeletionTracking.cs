using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailWinnow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddBlockedSourceDeletionTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BlockedSourceDeletedUtc",
                table: "SourceMessageHeaders",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BlockedSourceDeletionError",
                table: "SourceMessageHeaders",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BlockedSourceDeletionStartedUtc",
                table: "SourceMessageHeaders",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SourceMessageHeaders_EvaluationOutcome_BlockedSourceDeletedUtc_BlockedSourceDeletionStartedUtc",
                table: "SourceMessageHeaders",
                columns: new[] { "EvaluationOutcome", "BlockedSourceDeletedUtc", "BlockedSourceDeletionStartedUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SourceMessageHeaders_EvaluationOutcome_BlockedSourceDeletedUtc_BlockedSourceDeletionStartedUtc",
                table: "SourceMessageHeaders");

            migrationBuilder.DropColumn(
                name: "BlockedSourceDeletedUtc",
                table: "SourceMessageHeaders");

            migrationBuilder.DropColumn(
                name: "BlockedSourceDeletionError",
                table: "SourceMessageHeaders");

            migrationBuilder.DropColumn(
                name: "BlockedSourceDeletionStartedUtc",
                table: "SourceMessageHeaders");
        }
    }
}
