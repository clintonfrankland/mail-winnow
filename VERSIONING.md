# Mail Winnow versioning

Mail Winnow uses Semantic Versioning with a monotonically increasing build number.

- `VersionPrefix` is `MAJOR.MINOR.PATCH`.
- `BuildNumber` increases for every shipped task.
- The displayed assembly version is `MAJOR.MINOR.PATCH.BUILD`.

These values live in `src/MailWinnow.Web/MailWinnow.Web.csproj`. Bug fixes normally increment only `BuildNumber`; meaningful user-facing features also increment `VersionPrefix` according to Semantic Versioning.

GitHub Container Registry releases use `VersionPrefix` as the strict semantic image tag and also publish the exact tested 40-character Git SHA. Each future GHCR release must advance `VersionPrefix` so its semantic tag is new; `BuildNumber` remains monotonic and is retained only in the four-part assembly/display version. The workflow accepts only strict non-leading-zero semantic tags and verifies final registry tags against Buildx-produced or retained complete-preflight manifest digests.

The navigation displays the complete four-part assembly version so every shipped build is visible.
