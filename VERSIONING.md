# Mail Winnow versioning

Mail Winnow uses Semantic Versioning with a monotonically increasing build number.

- `VersionPrefix` is `MAJOR.MINOR.PATCH`.
- `BuildNumber` increases for every shipped task.
- The displayed assembly version is `MAJOR.MINOR.PATCH.BUILD`.

These values live in `src/MailWinnow.Web/MailWinnow.Web.csproj`. Bug fixes normally increment only `BuildNumber`; meaningful user-facing features also increment `VersionPrefix` according to Semantic Versioning.

The navigation displays the complete four-part assembly version so every shipped build is visible.
