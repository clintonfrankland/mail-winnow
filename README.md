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
