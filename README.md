# MailWinnow

MailWinnow is a self-hosted email filtering and selective-delivery platform for households.

## Solution layout

`MailWinnow.sln` is the repository entry point. Source projects live in `src/`; automated tests live in `tests/`.

- `MailWinnow.Web` is the Blazor host and the future authentication boundary.
- `MailWinnow.Worker` is the independently runnable scheduled IMAP-processing host.
- `MailWinnow.Core` contains dependency-free entities, interfaces, rule logic, and shared models.
- `MailWinnow.Infrastructure` contains implementations for EF Core, MailKit, encryption, and other external services. It may depend on Core, but Core never depends on it.
- `MailWinnow.Tests` contains unit and integration tests. It may reference production projects but is not referenced by them.

The hosts may reference Core and Infrastructure. Infrastructure may reference Core. These references establish the dependency direction and prevent circular dependencies.

## Development conventions

Use the .NET 10 SDK and run the solution from the repository root:

```sh
dotnet restore MailWinnow.sln
dotnet build MailWinnow.sln --no-restore
dotnet test MailWinnow.sln --no-build
```

`Directory.Build.props` applies nullable reference types, current analyzers, and warnings-as-errors to every project. Database entities and EF Core migrations are added only alongside an implemented feature; this foundation deliberately introduces neither.

## SQL Server provisioning

Server-level provisioning is intentionally separate from application migrations. The credential-free `tools/MailWinnow.SqlProvisioner` utility idempotently creates or reconciles one database, its dedicated SQL login and user, and the database-local `db_owner` role needed by the controlled EF Core migration process. It removes fixed server-role membership and denies database enumeration; it does not create application tables or run EF Core migrations.

Supply all inputs at runtime through environment variables:

```sh
MAILWINNOW_SQL_ADMIN_CONNECTION='<administrator connection>' \
MAILWINNOW_SQL_DATABASE='MailWinnow_Dev' \
MAILWINNOW_SQL_LOGIN='mailwinnow_dev' \
MAILWINNOW_SQL_PASSWORD='<strong generated password>' \
dotnet run --project tools/MailWinnow.SqlProvisioner
```

Use separate passwords for production and development. Store the resulting application connection strings only in Home Helm as secret `ConnectionStrings__MailWinnow` variables scoped to `Production` and `Review`; never commit them or deployed `.env` files.
