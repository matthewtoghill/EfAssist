# EfAssist — Plan

A cross-platform desktop GUI over the `dotnet ef` CLI, for managing Entity Framework Core migrations.

Status: planning. Nothing built yet.

Verified environment (2026-08-18, this machine):
- .NET SDK `10.0.400`
- `dotnet-ef` global tool `10.0.10`
- Avalonia templates **not** installed (`dotnet new install Avalonia.Templates` needed)

---

## 1. Project Spec

### 1.1 Goal

Remove the friction of EF Core migration work: no more remembering which project is `--project` vs `--startup-project`, no more guessing whether a migration is applied, no more copy-pasting long command lines.

### 1.2 Primary user

A .NET developer working in a solution with one or more `DbContext` types, who currently runs `dotnet ef` by hand or through Visual Studio's Package Manager Console.

### 1.3 In scope (v1)

| Area | Behaviour |
| --- | --- |
| Workspace | Open a folder, `.sln`, `.slnx`, or `.csproj`. Enumerate projects. Persist the chosen startup project + migrations project + context per workspace. |
| Context discovery | Populate a context dropdown from `dotnet ef dbcontext list --json`. |
| Migration list | Show migrations with applied / pending state from `dotnet ef migrations list --json`. |
| Add migration | Name field, optional `--output-dir`, run `migrations add`. |
| Remove migration | `migrations remove`, with `--force` behind a confirmation. |
| Update database | `database update`, optionally to a specific target migration (including `0`). |
| Script | `migrations script [from] [to] [--idempotent]`, shown in a read-only viewer with Copy and Save As. |
| Output console | Live stdout/stderr for the running command, cancellable. |
| Diagnostics | "Copy diagnostics" — command line, working directory, exit code, full output, tool + SDK versions. |
| Preflight checks | Detect missing `dotnet ef`, and a version mismatch between the tool and the project's `Microsoft.EntityFrameworkCore` reference. |

### 1.4 Out of scope (v1) — deliberately

- Packaging beyond `PublishSingleFile` (MSIX / DMG / AppImage parked, per the brief).
- Direct `__EFMigrationsHistory` querying — see §2.3, this is largely redundant and costs three database drivers.
- Editing migration `.cs` files, scaffolding models, `dbcontext scaffold` (reverse engineering).
- Multiple concurrent command execution. One command at a time, queued.
- Git integration, migration diffing, team-conflict detection.

### 1.5 Non-functional

- Never block the UI thread on a process.
- Every destructive action (`migrations remove`, `database update` to an earlier migration, `database drop`) requires explicit confirmation naming the target.
- Every command executed is visible verbatim, so the user can reproduce it in a terminal.
- No telemetry, no network calls.

---

## 2. Tech Stack

### 2.1 Agreed with the brief

| Choice | Verdict | Note |
| --- | --- | --- |
| .NET 10 | Yes | SDK already present. |
| Avalonia UI | Yes | Only credible cross-platform XAML option. Verify Avalonia 11.x targets `net10.0` cleanly as the first spike — if not, target `net9.0` for the UI project and keep Core on `net10.0`. |
| CommunityToolkit.Mvvm | Yes | `[ObservableProperty]` / `[RelayCommand]` source generators cover everything here. ReactiveUI's observable composition buys nothing for a form-and-list app and adds a learning tax. |
| System.Text.Json settings | Yes | In the box. |
| `ProcessStartInfo` | Yes | See §2.4 for the correct pattern. |

### 2.2 Changes from the brief

**Don't hand-parse `.sln` files.** `dotnet sln <path> list` returns the project list and handles both `.sln` and the new `.slnx` format for free. Fall back to globbing `**/*.csproj` when there's no solution file. Skipped: a solution-format parser.

**Don't use reflection for `DbContext` discovery.** `dotnet ef dbcontext list --json` already does it, correctly, including design-time factories. Loading the user's assemblies into our process would mean an `AssemblyLoadContext`, version conflicts, and a crash surface we don't want.

**Use `--json` everywhere it exists**, not text scraping. `migrations list`, `dbcontext list`, and `dbcontext info` all support it. This is the single most important decision in the plan — text scraping EF output would rot on every EF release.

**A core settings file plus one file per workspace.** `%AppData%/EfAssist/settings.json` holds app-wide preferences and the recent list; each workspace's choices live in `workspaces/<name>-<hash>.json`, named for the solution and hashed on the absolute path so same-named solutions in different repos can't collide. Workspace files are read lazily, on open, so startup cost doesn't grow with the number of workspaces ever used.

**One text viewer, not an editor.** A read-only `TextBox` for SQL in v1. AvaloniaEdit (syntax highlighting, folding) is a real dependency for a read-only pane — add it when someone actually complains.

### 2.3 On direct `__EFMigrationsHistory` querying

`dotnet ef migrations list --json` returns, per migration: `id`, `name`, `safeName`, and **`applied`** — it connects to the database itself and tells us what's applied. Adding `Microsoft.Data.SqlClient` + `Npgsql` + `MySqlConnector`, plus connection-string discovery and provider detection, to re-derive the same boolean is a lot of surface for no new information.

What direct querying *would* add: applied timestamps (not in the history table — it only stores `MigrationId` and `ProductVersion`, so there are no timestamps to show anyway), and the `ProductVersion` column. That's it.

**Recommendation:** cut it from v1. Use `--no-connect` when the user wants an offline list. Revisit only if a concrete need appears that `--json` can't answer.

*(This is the biggest scope reduction in the plan — flagging it explicitly because the brief asked for it. If you want it regardless, say so and it goes in as Phase 6, roughly one day plus three NuGet packages.)*

### 2.4 Process invocation — required pattern

Getting this wrong is the classic source of hangs and truncated output.

- Redirect stdout **and** stderr, and read both **asynchronously** (`OutputDataReceived` / `ErrorDataReceived` + `BeginOutputReadLine`). Synchronously reading one to completion before the other deadlocks when a child fills the other pipe's buffer.
- `UseShellExecute = false`, `CreateNoWindow = true`, `StandardOutputEncoding = Encoding.UTF8`.
- Invoke `dotnet` with `ef ...` arguments (not `dotnet-ef`), so a local tool manifest (`.config/dotnet-tools.json`) is honoured.
- Set `WorkingDirectory` to the solution directory and pass `--project` / `--startup-project` explicitly. Never rely on the CWD to select projects.
- Cancellation: `Process.Kill(entireProcessTree: true)`. MSBuild spawns children; killing only the parent leaves node processes behind.
- `--prefix-output` prefixes every line with `info:`, `data:`, `warn:`, or `error:`. Combined with `--json`, the JSON payload arrives on the `data:` lines, which makes it cleanly separable from build noise. Prefer this over "find the first `[`".

### 2.5 Solution layout

```
EfAssist.slnx
src/
  EfAssist.Core/          class library — no UI reference
    EfCli.cs                  build args, run process, stream output
    EfJson.cs                 DTOs + parsing of --json --prefix-output
    Workspace.cs              solution/project/context discovery
    Settings.cs               load/save AppData JSON
  EfAssist.App/           Avalonia + CommunityToolkit.Mvvm
tests/
  EfAssist.Core.Tests/    xunit — parsing + arg-building only
```

Three projects. Not a layered-architecture pyramid: there is one UI and one consumer of Core, so interfaces-per-service and a DI abstraction layer would be pure ceremony. `EfCli` is the one seam worth an interface, because tests need to fake process output.

---

## 3. Open Questions

Blocking (needed before Phase 2):

1. **Avalonia on `net10.0`** — does the current Avalonia release build and run against `net10.0`, or do we pin the UI to `net9.0`? Resolved by the Phase 0 spike, not by discussion.
A: Yes it builds against `net10.0`.

2. **`--json` shape on EF 10.0.10** — confirm the `applied` field is present and populated. The whole applied/pending feature rests on this. Also a Phase 0 spike.

Non-blocking, need a decision before the relevant phase:

3. **Multi-context solutions** — one context selected at a time, or a tree showing all contexts and their migrations side by side? (Assumption if unanswered: one at a time, dropdown.)
A: Let's start with one context selected at a time with a dropdown to switch between the selected context.

4. **Connection strings** — EF resolves these from the startup project's config/user-secrets. Do we ever want to override the connection (`--connection`) from the UI, e.g. to point a migration run at a different environment? This is powerful and dangerous in equal measure. (Assumption: no override in v1.)
A: I agree no override in v1, but let's also keep a ROADMAP.md file with these ideas in that we can come back to later.

5. **`--no-build` default** — building every time is slow but always correct; `--no-build` is fast but silently uses stale assemblies. (Assumption: build by default, `--no-build` as an explicit opt-in toggle with a warning.)
A: Agree that the default should be to build every time. However we should also include a checkbox for appropriate actions to specify no build.

6. **Where do generated SQL scripts go** — always Save As, or a configured scripts folder per workspace? (Assumption: Save As, with last-used directory remembered.)
A: We should have this as a configurable setting per workspace. The default setting is Save As, but the user can set a destination folder for the scripts to output to. After a script is created we should either make it easy to view the contents of the script within the app or open the folder or script file.

7. **Migration name conventions** — do you want an optional timestamp/prefix/ticket-number template for `migrations add`? Cheap to add, genuine daily value, but it's a preference.
A: The user should specify the name of the migration when they are adding a migration through the app UI.

8. **`database drop`** — include it? It's a foot-gun, but its absence means dropping to a terminal for a common dev-loop action. (Assumption: include, behind a type-the-database-name confirmation.)
A: Agree, we can add it but require the user to type-the-database-name in a confirmation window. 

9. **Target platforms for CI/publish** — Windows only for now, or all three from day one?
A: Windows only for now. Add the others to the ROADMAP.md file.

---

## 4. Implementation Plan

Each phase ends with something runnable. Phase 0 exists to kill the two unknowns before any code depends on them.

### Phase 0 — Spikes (half a day)

- Install templates: `dotnet new install Avalonia.Templates`. Create a throwaway `avalonia.mvvm` app, retarget to `net10.0`, confirm it builds and shows a window. Record the answer in this document.
- Against any EF project (scaffold a two-minute throwaway one with SQLite if needed), capture real output for: `dotnet ef migrations list --json --prefix-output`, `dbcontext list --json --prefix-output`, and a failing command. **Save these as text fixtures in `tests/`** — they become the parser tests and they're the only defence against EF changing its output.
- Confirm `applied` is present and correct in the `migrations list` JSON.

Exit criteria: target frameworks decided, fixtures committed.

### Phase 1 — Core: process + parsing (1–2 days)

- `EfCli`: argument builder (`--project`, `--startup-project`, `--context`, `--json`, `--prefix-output`, `--no-build`, `--configuration`, `--framework`) and the async process runner from §2.4, with `IProgress<OutputLine>` for streaming and `CancellationToken` for kill-tree.
- `EfJson`: DTOs for migrations list and dbcontext list, plus the `data:`-line extraction.
- `Workspace`: `dotnet sln list` with a `**/*.csproj` fallback; heuristics to pre-select the startup project (the one with `Microsoft.EntityFrameworkCore.Design`, or an `OutputType` of `Exe`) and the migrations project (the one containing a `Migrations/` folder).
- `Settings`: load/save, atomic write (temp file + `File.Move` overwrite) so a crash mid-write doesn't destroy the file.
- Tests: arg building for every command; parsing against the Phase 0 fixtures; parsing a malformed/empty response without throwing.

Exit criteria: `dotnet test` green. Core has no Avalonia reference.

### Phase 2 — Shell + workspace picker (1 day)

- Avalonia app, single window: left pane = workspace tree / project + context selectors; right pane = tabs; bottom = output console with a Cancel button.
- Open folder / solution / project. Recent workspaces list from settings.
- Selections persist and restore on reopen.
- Preflight banner: `dotnet ef` missing (offer the exact `dotnet tool install --global dotnet-ef` line to copy), or tool/package major version mismatch.

Exit criteria: open a real solution, contexts populate in the dropdown.

### Phase 3 — Migrations tab (1–2 days)

- List with applied/pending state and a refresh button.
- `Add`: name (validated — non-empty, valid C# identifier), optional output dir.
- `Remove`: acts on the last migration only; confirmation dialog names it; `--force` as an explicit checkbox with its own warning.
- `Update database`: to latest, or to a selected target. Downgrade and `0` require a confirmation naming the target.
- Offline mode toggle → `--no-connect`.

Exit criteria: full add → update → remove round trip against a real database, from the GUI only.

### Phase 4 — Script tab (half a day)

- From / to migration pickers (default: pending range), `--idempotent` checkbox.
- Read-only viewer, Copy, Save As.

Exit criteria: generated script matches what the CLI produces for the same arguments.

### Phase 5 — Errors, diagnostics, polish (1 day)

- Map the common EF failures to plain-language guidance, with the raw output always one click away:
  - "No project found" / wrong `--startup-project`
  - "Unable to create a 'DbContext'" (missing design-time factory or a failing `Program`)
  - Login / network failures reaching the database
  - "Your target project doesn't match your migrations assembly"
  - Tool-vs-package version mismatch
- "Copy diagnostics" produces one pasteable block: command, working directory, exit code, tool version, SDK version, full output.
- Non-zero exit never leaves the UI in a lying state (stale list, spinner stuck on).

Exit criteria: each mapped error reproduced deliberately and shown correctly.

### Phase 6 — Publish (half a day)

- `dotnet publish -r <rid> -c Release --self-contained`.
- **Do not enable `PublishTrimmed`.** Avalonia's XAML loading and CommunityToolkit's generated code are trim-hostile enough that the failure mode is a runtime crash in a shipped binary, not a build error. Not worth the megabytes.

Exit criteria: a binary runs on a machine without the .NET SDK.

**Amended during implementation.** The brief for this phase asked for an installer that can also update the app, which pulls the "Installer packaging" and auto-update items forward from the roadmap. Velopack does both, so Phase 6 became: self-contained publish → `vpk pack` → installer, portable zip and delta package, plus an in-app updater that reads GitHub Releases. Two consequences for the bullets above:

- **`PublishSingleFile` is off, not on.** Velopack packs a directory and produces binary deltas against the previous release; a single bundled exe defeats that, so every update would re-download the whole application.
- **Nothing is signed.** SmartScreen will warn on first run of the installer until download reputation builds. A code-signing certificate is the only real fix; `vpk pack --signParams` is where it would go.

**Rough total: 6–8 working days** for v1 as scoped, assuming the Phase 0 spikes come back clean.

---

## 5. Risks

| Risk | Mitigation |
| --- | --- |
| EF changes its `--json` shape between versions | Fixture-based parser tests; parse defensively (unknown fields ignored, missing fields non-fatal). |
| Avalonia not ready for `net10.0` | Phase 0 spike; fall back to `net9.0` for the UI project only. |
| Orphaned MSBuild processes after cancel | `Kill(entireProcessTree: true)`, verified manually on each OS. |
| Users' solutions are stranger than our heuristics | Heuristics only *pre-select*; every selection stays manually overridable. |
| Slow builds make the app feel broken | Stream output live from the first line; always show a cancel button. |
