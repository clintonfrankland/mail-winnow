using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailWinnow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScopeReviewDecisionIdempotencyToActiveWorkItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReviewDecisionWorkItems_OwnerUserId_IdempotencyKey",
                table: "ReviewDecisionWorkItems");

            migrationBuilder.CreateIndex(
                name: "IX_ReviewDecisionWorkItems_OwnerUserId_IdempotencyKey",
                table: "ReviewDecisionWorkItems",
                columns: new[] { "OwnerUserId", "IdempotencyKey" },
                unique: true,
                filter: "[Status] IN ('Pending', 'Processing', 'Retrying')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReviewDecisionWorkItems_OwnerUserId_IdempotencyKey",
                table: "ReviewDecisionWorkItems");

            migrationBuilder.CreateIndex(
                name: "IX_ReviewDecisionWorkItems_OwnerUserId_IdempotencyKey",
                table: "ReviewDecisionWorkItems",
                columns: new[] { "OwnerUserId", "IdempotencyKey" },
                unique: true);
        }
    }
}
