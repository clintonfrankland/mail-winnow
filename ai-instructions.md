# AI Instructions

## Core Rules

1. **Plan before you touch code.** No guessing. Write the plan first, execute second, re-plan when needed.
2. **Use parallel help when the task is large.** Break hard problems down and keep context clean.
3. **Make the system self-improving.** When a mistake teaches something durable, update the instructions.
4. **Test every change.** After every code change, evaluate what tests are appropriate, add them, and run the full test suite.
5. **Treat bugs as urgent.** Trace, root-cause, fix, and verify.
6. **Version bump every task.** Update the project version file using the repo's versioning rules before you call the work done.
7. **Keep README.md current.** Update `README.md` in the repo root whenever behavior, schema, routes, uploads, or user-facing features change. This is as non-negotiable as the version bump — do it in the same task.

## Versioning

Use Semantic Versioning with an explicit build number for every development project, including web apps and CLI tools.

### Current scheme

- Product version: `MAJOR.MINOR.PATCH`.
- Build number: monotonically increasing integer for shipped work.
- Display/package version: `MAJOR.MINOR.PATCH.BUILD` when the project supports a four-part version.
- Assembly/file version: `MAJOR.MINOR.PATCH.BUILD` when the project exposes assembly metadata.

### Where the version lives

- Prefer the repo's existing version file or main shipped project file.
- Web apps: update the primary web `.csproj`.
- CLI repos: update the shared props file if one exists; otherwise update each shipped CLI `.csproj` that produces a production binary. Do not version test projects unless they are independently shipped.
- If a repo has `VersionPrefix` and `BuildNumber`, treat `VersionPrefix` as `MAJOR.MINOR.PATCH` and increment `BuildNumber` for every task.
- If a repo only has `<Version>MAJOR.MINOR.PATCH.BUILD</Version>`, increment the final build segment for every task.
- If no version field exists yet, add a clear version property to the shipped project before calling the task done.

### When making changes

1. Increment the build number for every task before calling the work done.
2. For bug fixes, normally keep `MAJOR.MINOR.PATCH` unchanged and increment only the build number.
3. For new features, increment `MINOR` when the feature is user-visible or operationally meaningful; reset `PATCH` to `0` when incrementing `MINOR`.
4. For backwards-compatible fixes that warrant a patch release, increment `PATCH` and reset the build number only if that repo already follows that convention; otherwise keep the build number monotonic.
5. For breaking changes, increment `MAJOR` intentionally and document the reason.
6. Keep README/version references current when the versioning scheme or displayed version changes.

## C# Coding Conventions

- Use file-scoped namespaces.
- Prefer `var` when the type is obvious.
- Use async APIs consistently — do not mix sync and async paths on the same resource.
- Never abbreviate variable or method names. Names should be fully spelled out and as descriptive and human-readable as possible.
- Reusable logic belongs in shared libraries, not tool-specific or page-specific files.
- Keep component logic in `.razor.cs` code-behind files where the project uses Blazor.

## Things You Must Not Do

- Do not skip build and test verification.
- Do not bypass logging conventions.
- Do not break workspace path conventions.
- Do not hand-wave idempotency in automation tools.
- Do not hold DbContexts across renders or across awaits.
- Do not introduce another UI component library where one is already established.

## Workflow

### Plan Mode
- Start complex work with a plan.
- Re-plan instead of thrashing when something turns sideways.

### Implementation
1. Read relevant files first.
2. Follow existing patterns.
3. Run the build after changes.
4. Run all tests before calling work done.

### Parallel Work
- Use parallel help for isolated subtasks.
- Only one editor should change a given file at a time.

### Session Management
- Keep side investigations out of the main implementation thread.
- Fork sessions when it improves clarity.

### Multi-Repo Work
- Be explicit when work spans multiple repositories.
- Verify assumptions before copying patterns across repos.

### Automation
- Keep automation explicit, inspectable, and documented.
- Update any automation docs when workflows change.
- Prefer deterministic logging and retry behavior in cron tools.
- Use timeout-aware process helpers instead of raw process spawning.

## Documentation

`README.md` (repo root) and `docs/` are the canonical project docs. `README.md` is the front door — it must always reflect the current state of the project. Update it in the same task that changes architecture, endpoints, uploads, schemas, or user flows. Never leave it stale.

### What README.md must contain

Keep these sections current. Do not remove them; do not let them drift.

1. **Project overview** — one paragraph stating purpose and audience.
2. **Architecture diagram** — Mermaid diagram showing the system boundary and all key components.
3. **Technology section** — table of every technology, library, and framework with its version. Pull versions from project files.
4. **Domain concepts** — brief definitions of the key entities and domain terms used throughout the codebase.
5. **User workflows** — one Mermaid flowchart per distinct user type tracing every discernible path through the application.
6. **File reference table** — every significant source file in every directory, with a one-line description. Group by folder. Do not omit services, pages, models, or config files.
7. **Coding conventions** — project-specific conventions beyond the generic C# rules in these instructions.
8. **Constraints** — hard rules about what must not change or be introduced (e.g. preserved API contracts, locked dependencies).
9. **Known pitfalls** — non-obvious traps a developer would hit: wrong paths, environment differences, legacy exclusions.
10. **SQL objects** — table listing every database table with a brief description. Include views, stored procedures, and functions if they exist.
11. **External dependencies** — two tables: project references first, then package references per project. Pull versions from project files.

### Standards

- Use GitHub-flavoured Markdown throughout.
- Use Mermaid diagrams — they render natively on GitHub and in most IDEs. Prefer `flowchart TD` or `flowchart LR` for workflows; `graph TB` for component/system diagrams.
- Keep documentation focused on this project. Do not pad with generic advice.
- Add supplementary detail under `docs/` when a topic is too large for the README.
- Keep operational documentation aligned with actual code.
- When workflow behavior changes, update the relevant docs in the same task.

## Self-Improvement

After every correction or durable lesson, update this file or the repo's canonical instructions so the same mistake is less likely to repeat.

