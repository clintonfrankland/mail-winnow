using MailWinnow.Worker;
using MailWinnow.Infrastructure.Persistence;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddMailWinnowSqlServer(builder.Configuration);
builder.Services.AddHostedService(provider => provider.GetRequiredService<MailWinnow.Infrastructure.Rules.ReviewDecisionQueue>());
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
