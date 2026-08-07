using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailWinnow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MoveApprovedMessagesAndOptionalRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SourceDeletedUtc",
                table: "MessageDeliveries",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "DeliveredMessageRetentionDays",
                table: "MailRules",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int",
                oldDefaultValue: 30);

            // The prior UI hard-coded 30 for every created rule. It was not an
            // informed retention choice, so move those legacy defaults to Forever.
            migrationBuilder.Sql("UPDATE [MailRules] SET [DeliveredMessageRetentionDays] = NULL WHERE [DeliveredMessageRetentionDays] = 30;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE [MailRules] SET [DeliveredMessageRetentionDays] = 30 WHERE [DeliveredMessageRetentionDays] IS NULL;");

            migrationBuilder.DropColumn(
                name: "SourceDeletedUtc",
                table: "MessageDeliveries");

            migrationBuilder.AlterColumn<int>(
                name: "DeliveredMessageRetentionDays",
                table: "MailRules",
                type: "int",
                nullable: false,
                defaultValue: 30,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);
        }
    }
}
