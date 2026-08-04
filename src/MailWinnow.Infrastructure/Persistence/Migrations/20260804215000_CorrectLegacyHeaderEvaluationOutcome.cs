using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MailWinnow.Infrastructure.Persistence.Migrations;

/// <summary>
/// Corrects the original rule-engine migration's Allow default. Catalogued headers
/// that existed before rules were introduced have not been evaluated and must be
/// reconsidered when a rule is created.
/// </summary>
[DbContext(typeof(MailWinnowDbContext))]
[Migration("20260804215000_CorrectLegacyHeaderEvaluationOutcome")]
public class CorrectLegacyHeaderEvaluationOutcome : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "UPDATE [SourceMessageHeaders] " +
            "SET [EvaluationOutcome] = 2 " +
            "WHERE [EvaluationOutcome] = 0 AND [EvaluatedUtc] IS NULL;");

        migrationBuilder.AlterColumn<int>(
            name: "EvaluationOutcome",
            table: "SourceMessageHeaders",
            type: "int",
            nullable: false,
            defaultValue: 2,
            oldClrType: typeof(int),
            oldType: "int",
            oldDefaultValue: 0);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<int>(
            name: "EvaluationOutcome",
            table: "SourceMessageHeaders",
            type: "int",
            nullable: false,
            defaultValue: 0,
            oldClrType: typeof(int),
            oldType: "int",
            oldDefaultValue: 2);
    }
}
