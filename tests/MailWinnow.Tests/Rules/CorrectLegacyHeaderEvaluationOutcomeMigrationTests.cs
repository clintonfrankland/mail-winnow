using MailWinnow.Infrastructure.Persistence.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace MailWinnow.Tests.Rules;

public sealed class CorrectLegacyHeaderEvaluationOutcomeMigrationTests
{
    [Fact]
    public void Up_QueuesPreexistingUnevaluatedAllowHeadersForReevaluation()
    {
        var migration = new InspectableMigration();
        var operations = migration.GetUpOperations();

        var correction = Assert.IsType<SqlOperation>(operations[0]);
        Assert.Contains("[EvaluationOutcome] = 2", correction.Sql);
        Assert.Contains("[EvaluationOutcome] = 0", correction.Sql);
        Assert.Contains("[EvaluatedUtc] IS NULL", correction.Sql);

        var defaultChange = Assert.IsType<AlterColumnOperation>(operations[1]);
        Assert.Equal(2, defaultChange.DefaultValue);
        Assert.Equal(0, defaultChange.OldColumn.DefaultValue);
    }

    private sealed class InspectableMigration : CorrectLegacyHeaderEvaluationOutcome
    {
        public IReadOnlyList<MigrationOperation> GetUpOperations()
        {
            var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
            Up(builder);
            return builder.Operations;
        }
    }
}
