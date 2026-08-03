using MailWinnow.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var configuration = new ConfigurationBuilder()
    .AddEnvironmentVariables()
    .Build();

var connectionString = configuration.GetConnectionString("MailWinnow")
    ?? throw new InvalidOperationException(
        "The ConnectionStrings:MailWinnow configuration value is required. Set ConnectionStrings__MailWinnow.");

var services = new ServiceCollection();
services.AddLogging(logging => logging.AddSimpleConsole(options => options.SingleLine = true));
services.AddMailWinnowSqlServer(configuration);

await using var serviceProvider = services.BuildServiceProvider();
var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("MailWinnow.DbMigrator");

try
{
    await using var scope = serviceProvider.CreateAsyncScope();
    var context = scope.ServiceProvider.GetRequiredService<MailWinnowDbContext>();
    var pendingMigrations = await context.Database.GetPendingMigrationsAsync();
    var pendingMigrationNames = pendingMigrations.ToArray();

    await context.Database.MigrateAsync();
    logger.LogInformation(
        "Database migration completed. Applied {AppliedMigrationCount} pending migration(s).",
        pendingMigrationNames.Length);
}
catch (Exception exception)
{
    logger.LogCritical(exception, "Database migration failed.");
    Environment.ExitCode = 1;
}
