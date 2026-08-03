using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MailWinnow.Infrastructure.Persistence;

/// <summary>
/// Supplies the context to EF tooling without starting either long-running host.
/// </summary>
public sealed class MailWinnowDbContextFactory : IDesignTimeDbContextFactory<MailWinnowDbContext>
{
    public MailWinnowDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__MailWinnow")
            ?? throw new InvalidOperationException(
                "The ConnectionStrings__MailWinnow environment variable is required for EF tooling.");

        var options = new DbContextOptionsBuilder<MailWinnowDbContext>()
            .UseSqlServer(connectionString)
            .Options;

        return new MailWinnowDbContext(options);
    }
}
