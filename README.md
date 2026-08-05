# MailWinnow

MailWinnow is a self-hosted email filtering and selective-delivery platform for households.

## Solution layout

`MailWinnow.sln` is the repository entry point. Source projects live in `src/`; automated tests live in `tests/`.

- `MailWinnow.Web` is the Blazor host and the future authentication boundary.
- `MailWinnow.Worker` is the independently runnable scheduled IMAP-processing host.
- `MailWinnow.Core` contains dependency-free entities, interfaces, rule logic, and shared models.
- `MailWinnow.Infrastructure` contains implementations for EF Core, MailKit, encryption, and other external services. It may depend on Core, but Core never depends on it.
- `MailWinnow.Tests` contains unit and integration tests. It may reference production projects but is not referenced by them.
- `MailWinnow.DbMigrator` is the one-shot deployment utility that applies EF Core migrations.

The hosts may reference Core and Infrastructure. Infrastructure may reference Core. These references establish the dependency direction and prevent circular dependencies.

## Development conventions

Use the .NET 10 SDK and run the solution from the repository root:

```sh
dotnet restore MailWinnow.sln
dotnet build MailWinnow.sln --no-restore
dotnet test MailWinnow.sln --no-build
```

`Directory.Build.props` applies nullable reference types, current analyzers, and warnings-as-errors to every project. The initial EF migration intentionally contains no application tables because no application entity model has been implemented yet.

## Application migrations

The application uses EF Core with SQL Server. `MailWinnowDbContext` reads the standard `ConnectionStrings:MailWinnow` configuration value, supplied in deployed environments through the scoped `ConnectionStrings__MailWinnow` variable. No host applies migrations during normal startup.

After database provisioning, run the one-shot migrator separately in each isolated environment:

```sh
ConnectionStrings__MailWinnow='<application connection>' \
dotnet run --project tools/MailWinnow.DbMigrator
```

The migrator applies pending migrations, reports failures with a non-zero exit code, and is safe to run again. Verify the resulting `__EFMigrationsHistory` entry before continuing from development to production.

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

## Credential protection

Mailbox passwords, app passwords, destination passwords, and future OAuth refresh tokens must be stored only through `ICredentialProtectionService`. The abstraction uses purpose-separated ASP.NET Core Data Protection payloads and never exposes a read/display model. A missing key-ring configuration or mount fails host startup, while corrupt payloads and unavailable keys raise a safe `CredentialProtectionException` without including plaintext.

Both Web and Worker read `DataProtection:KeyRingPath` (`DataProtection__KeyRingPath` as an environment variable) and use the fixed `MailWinnow` application discriminator. In containers, merge `deploy/compose.data-protection.yaml` into the deployment Compose configuration so both services mount the same named volume. Back up this volume with the database: losing its key files makes saved credentials intentionally undecryptable.

## Destination IMAPS service

[`deploy/dovecot`](deploy/dovecot/README.md) contains a standalone Dovecot IMAPS Compose template for household destination mailboxes. It deliberately excludes SMTP and Postfix; use it only as the local IMAP target for approved messages. The template documents password-file users, TLS, persistence, backups, and its disposable smoke test.
