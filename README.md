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

Blazor query-string filters must bind only framework-supported scalar types. For enum filters, bind the raw query value as `string`, parse it explicitly with `Enum.TryParse`, and treat missing or invalid values as an unfiltered request. Keep regression coverage for missing, valid, case-insensitive, and invalid values so a filter cannot prevent its page from rendering.

HTML checkboxes are omitted from form submissions when unchecked. Login form request models must therefore use a property-bound model with `RememberMe` defaulting to `false`; do not make the checkbox a required constructor-bound value. Run `scripts/smoke-login-form.sh <base-url>` after deployment to verify that both unchecked and checked submissions reach the login handler without an HTTP 400 response. The smoke test deliberately uses invalid credentials and never accepts a password argument.

Mailbox forms follow the same property-binding rule. New source-account submissions omit `Id`, unchecked TLS/enabled/discovery fields are absent, and selecting no folders omits `SelectedFolders`. Keep these request models parameterless with safe property defaults so omitted optional fields reach the handler instead of failing model binding. Run `scripts/check-mailbox-form-contract.sh` before deployment; it verifies both the rendered form shape and the property-bound request contract.

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

Both Web and Worker read `DataProtection:KeysPath` (`DataProtection__KeysPath` as an environment variable) and use the fixed `MailWinnow` application discriminator. In containers, the deployment Compose configuration mounts the same named volume into both services. Back up this volume with the database: losing its key files makes saved credentials intentionally undecryptable.

## Destination IMAPS service

[`deploy/dovecot`](deploy/dovecot/README.md) contains a standalone Dovecot IMAPS Compose template for household destination mailboxes. It deliberately excludes SMTP and Postfix; use it only as the local IMAP target for approved messages. The template documents password-file users, TLS, persistence, backups, and its disposable smoke test.

## Home Helm container deployment

[`deploy/docker-compose.home-helm.yml`](deploy/docker-compose.home-helm.yml) builds separate Web and Worker images plus a profile-gated one-shot migrator. All three run as the .NET image's non-root application user. Web listens on container port 8080 and exposes an anonymous `GET /healthz` check that returns 200 only when its scoped database is reachable. Worker writes its heartbeat to the shared database; administrators can observe it on the Web Operations page. Application logs go to stdout/stderr. Do not log configuration dumps, connection strings, credentials, or message bodies.

Home Helm must maintain separate managed runtime env files for Production and Review. In each scope, set `ConnectionStrings__MailWinnow` to that scope's database and never copy one managed env file over the other. Compose requires the variable and passes it through the shared environment block to both Web and Worker. Use distinct Compose project names and named volumes per environment so Review cannot share Production's database or Data Protection keys.

The complete non-secret contract is in [`deploy/mailwinnow.env.example`](deploy/mailwinnow.env.example). Production must retain these values:

```text
LocalImap__Host=mailwinnow-imap.clintandtara.com
LocalImap__Port=993
LocalImap__UseSsl=true
LocalImap__AllowInvalidCertificate=false
```

`MailSync__PollingIntervalSeconds`, `MailSync__BatchSize`, and `MailSync__MaximumConcurrency` control Worker scheduling. Destination mailbox passwords are stored per household user through the application's encrypted credential store. They do not belong in the shared runtime env file. Delivery uses IMAP APPEND; this deployment adds no SMTP service.

Build and start an environment from its Home Helm-managed env file without printing its contents:

```sh
docker compose --project-name mailwinnow-production \
  --env-file /path/to/home-helm/production.env \
  -f deploy/docker-compose.home-helm.yml up -d --build web worker
```

Run migrations only as an explicit, fail-fast one-shot operation before normal startup or during an approved deployment:

```sh
docker compose --project-name mailwinnow-production \
  --env-file /path/to/home-helm/production.env \
  -f deploy/docker-compose.home-helm.yml --profile migration run --rm migrate
```

For each environment, verify from both actual application containers that DNS resolves and the public TLS chain validates. `openssl s_client` must exit successfully without `-verify_none`; install/use an ephemeral diagnostic container in the same Compose network if the minimal runtime image lacks these tools. Then request `/healthz` and confirm the Operations page shows a current Worker heartbeat. Confirm database identity using a non-secret database name query from each container network, and compare it with the expected scoped database. Never print the connection variable itself in deployment output.

The credential-safe `MailWinnow.DeploymentProbe` performs those database, DNS, TCP, and strict TLS checks using the actual container environment. Publish it, copy the output directory into each running Web and Worker container, and execute `dotnet MailWinnow.DeploymentProbe.dll` there. Its JSON output contains only the database name, connectivity booleans, heartbeat freshness, TLS protocol, and the invalid-certificate-bypass state. It never emits the connection string or mailbox credentials.
