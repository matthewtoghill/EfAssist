# EfMigrateHub — Progress

What is actually built and verified. `PLAN.md` is the agreed plan and spec; this is the record of implementation, and the only place implementation detail is written down.

Last updated: 2026-08-19.

| Phase | Status |
| --- | --- |
| 0 — EF output spike + scaffold | **Done** |
| 1 — Core: process + parsing | **Done** |
| 2 — Shell + workspace picker | **Done** |
| 3 — Migrations tab | **Done** |
| 4 — Script tab | **Done** |
| 5 — Errors, diagnostics, polish | **Done** |
| 6 — Publish + installer + in-app update (Windows) | **Done** |

Current state: `dotnet test EfMigrateHub.slnx` → **314 passed, 0 failed**, about 70 seconds. The app launches, opens a real solution or folder, lists migrations with their applied state, can add, apply, roll back, remove and drop, generates SQL scripts into a syntax-highlighted viewer, and explains the common EF failures in plain language without hiding the raw output.

Phases 2, 3 and 4 have each had a round of manual UI testing and follow-up fixes — see the review sections below.

---

## Phase 0 — Done

### Built

- `EfMigrateHub.slnx` with three projects, all on `net10.0`:
  - `src/EfMigrateHub.Core` — class library, **no package references at all**
  - `src/EfMigrateHub.App` — Avalonia 12.1.1 + CommunityToolkit.Mvvm (template default, untouched so far)
  - `tests/EfMigrateHub.Core.Tests` — xunit
- `samples/SampleEfApp` — SQLite EF Core project, deliberately outside the solution. Two contexts (`BlogContext`, `AuditContext`), two migrations, only the first applied. See its `README.md`.
- `.gitignore` (dotnet template) plus ignores for the sample's SQLite files.

### Verified

- `dotnet build EfMigrateHub.slnx` → 0 warnings, 0 errors.
- **Q2 answered: yes** — `dotnet ef migrations list --json` returns a populated `applied` field on EF 10.0.10. The decision to ship no database drivers stands.
- Nine fixtures captured from the real CLI into `tests/EfMigrateHub.Core.Tests/Fixtures/`:

| Fixture | What it captures |
| --- | --- |
| `migrations-list-mixed` | One applied, one pending |
| `migrations-list-noconnect` | `--no-connect` → `"applied": null` |
| `migrations-list-empty` | Context with no migrations → `[ ]` |
| `dbcontext-list` | Two contexts |
| `dbcontext-info` | Provider, database name, data source |
| `error-unknown-context` | `No DbContext named 'X' was found.` |
| `error-no-dbcontext` | Missing `Microsoft.EntityFrameworkCore.Design` |
| `script-plain` | Generated SQL on `data:` lines |
| `script-idempotent` | SQLite rejecting `--idempotent`, with stack trace |

Ten findings recorded in `PLAN.md` §3.1. Several changed the Phase 1 design.

### Security note

EF Core 10.0.10's SQLite provider pulls `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 transitively, which carries a high-severity advisory (NU1903 / GHSA-2m69-gcr7-jv3q). Pinned to `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5 in the sample project; builds and runs clean. Sample-only — the shipped app references no database drivers, so it never pulls this in.

---

## Phase 1 — Done

### Built

`src/EfMigrateHub.Core`, 6 files, no dependencies beyond the BCL:

| File | Contents |
| --- | --- |
| `OutputLine.cs` | `OutputChannel` enum + `OutputLine.Parse` — splits the fixed 9-character `--prefix-output` field |
| `EfResult.cs` | Exit code, classified lines, `Data`, `ErrorMessage`, `Diagnostics` |
| `EfArgs.cs` | `EfTarget` record + argument builders for all eight commands |
| `EfRunner.cs` | `IEfRunner` + the real process runner |
| `EfJson.cs` | `MigrationState`, `MigrationInfo`, `DbContextRef`, `DbContextDetails`, deserialization |
| `Workspace.cs` | Solution/project discovery and startup/migrations project heuristics |
| `Settings.cs` | `WorkspaceSettings`, `AppSettings`, `SettingsStore` |

Commands covered by `EfArgs`: `migrations list`, `migrations add`, `migrations remove`, `migrations script`, `database update`, `database drop`, `dbcontext list`, `dbcontext info`.

### Decisions made while building

These go beyond what `PLAN.md` specified and are worth knowing:

- **`--no-color` on every command**, alongside `--prefix-output`. Verified accepted by the CLI. Keeps ANSI escapes out of the output console.
- **`DOTNET_CLI_UI_LANGUAGE=en`** is set on the child process. The Phase 5 error mapping matches on English message text; without this it silently stops working on a localised machine.
- **`database drop` always passes `--force`.** Without it the CLI prompts on stdin, and a GUI-launched process would hang forever with no visible reason. The type-the-database-name confirmation is the app's job, not the CLI's. Stdin is also closed immediately after start as a second line of defence against any unexpected prompt.
- **`migrations script` always uses `--output <file>`**, never stdout (§3.1 finding 4).
- **`MigrationsScript` throws if given `to` without `from`.** They are positional, so a lone `to` would be silently read as `from` and generate a different script than asked for.
- **`Process.Start` failure returns a failed `EfResult`, not an exception.** That is the "dotnet is not on PATH" case, and callers should handle it through the same diagnostics path as any other failure.
- **Cancellation waits for the process without the token**, then throws. This collects whatever output arrived before the kill instead of discarding it.
- **`IEfRunner` is reused for `dotnet sln list`.** It runs `dotnet` with whatever arguments it is given; `Workspace` needs no second abstraction.
- **`DbContextDetails.ConfirmationName`** falls back to `dataSource` when `databaseName` is a generic provider constant (SQLite reports `"main"`).
- **`DbContextDetails.SupportsIdempotentScripts`** excludes SQLite and gives unknown providers the benefit of the doubt — better to attempt and show a clear error than to grey out a button that would have worked.
- **Corrupt settings are moved to `settings.json.corrupt`** rather than overwritten, so a user who cares can recover them.

### Verified

- **56 tests, all passing**, in under 1 second:
  - `OutputLineTests` — 8: prefix splitting per channel, SQL indentation preserved, blank `data:` line kept, unprefixed MSBuild output classified as `Raw`, text that merely starts with a token not mistaken for a prefix.
  - `EfJsonTests` — 11: applied/pending from the real fixture, **`applied: null` → `Unknown`, never `Pending`**, empty list is not a failure, contexts and provider details parsed, confirmation-name fallback, idempotent-support gate, malformed/empty payloads return null instead of throwing, unknown fields ignored.
  - `EfResultTests` — 6: error message is the `error:` line and excludes the `info:` stack trace, all three captured failures read cleanly, SQL reassembled with indentation, diagnostics block complete, fallback to unprefixed output when nothing is prefixed.
  - `EfArgsTests` — 11: every command requests prefixed uncoloured output and passes projects/context explicitly, `--json` only where supported, positional arguments land immediately after the verb, drop always forces, script always writes to a file, `to`-without-`from` rejected, `--no-connect`/`--no-build` opt-in, optional fields omitted when unset.
  - `WorkspaceTests` — 8: uses `dotnet sln list` rather than parsing a solution, finds a solution from a folder, falls back to globbing when `sln list` fails and skips `bin`/`obj`, a single project is a valid workspace, startup-project heuristics (Design package, then executable), migrations-project heuristic, defaulting.
  - `SettingsTests` — 6: round trip, case-insensitive and path-normalised workspace keys, recent list ordering/dedup/cap, corrupt file preserved, no temp file left behind.
  - `EfRunnerTests` — 5: real process output and exit code captured, streaming via `IProgress`, a failing command returns a result rather than throwing, an unstartable process returns a failed result, cancellation throws `OperationCanceledException`.
- **`EfMigrateHub.Core` has zero package references.** No Avalonia, no database drivers.
- Every flag combination `EfArgs` produces was run against `samples/SampleEfApp` by hand and exits 0, including `--no-color` and `--output`. The written `.sql` file starts at `CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (` with no prefix, confirming the file route gives byte-exact SQL.

### Not done in Phase 1

- **Preflight checks** (missing `dotnet ef`, tool-vs-package version mismatch) are listed under Phase 2 in the plan and were left there. Nothing in Core detects them yet.
- **Cancellation kills the process tree** — the code passes `entireProcessTree: true`, but the test only covers the "reports cancellation" half. Verifying no orphaned MSBuild nodes survive needs a long-running real command and is a manual check, deferred to Phase 3 when there is a UI to cancel from.
- No UI work of any kind. `src/EfMigrateHub.App` is still the unmodified template.

---

## Phase 2 — Done

### Answers that shaped this phase

Four questions were put to the user before implementation:

1. **When does context discovery run?** Auto on open and cancellable, *plus* a Refresh button, *plus* the choice exposed as a per-workspace setting covering all three options. Implemented as `DiscoveryMode` in `WorkspaceSettings`: `Auto`, `Manual`, `AutoNoBuildFirst`.
2. **EF tool version mismatch check?** Dropped — a deliberate scope reduction from §1.3 of the plan. Only tool *presence* is checked. EF already reports a mismatch itself with a clear message, and reproducing the check meant either an unreliable file scan or another slow restore.
3. **On launch?** A recent-workspaces landing screen. Nothing builds until the user picks something.
4. **Where does implementation detail go?** This file only. `PLAN.md` stays the agreed plan and spec.

### Built

Core additions:

| File | Contents |
| --- | --- |
| `Preflight.cs` | `ToolStatus` + `Preflight.CheckAsync` — probes `dotnet --version` and `dotnet ef --version` |
| `Settings.cs` | `DiscoveryMode` enum, `WorkspaceSettings.Discovery`, enum-as-name JSON |

App, now a real shell:

| File | Contents |
| --- | --- |
| `ViewModels/MainWindowViewModel.cs` | The whole shell: landing state, workspace loading, selections, discovery, output console, diagnostics |
| `Views/MainWindow.axaml` | Preflight banner, toolbar, landing panel, workspace panel, output console, status bar |
| `Views/MainWindow.axaml.cs` | File pickers, clipboard, output auto-scroll |
| `OutputChannelBrushConverter.cs` | Colours console lines by channel |

Deleted from the template: `ViewLocator.cs`, `ViewModels/MainViewModel.cs`, `ViewModels/ViewModelBase.cs`, and the `Models\` placeholder folder. One window and one view model means a view locator is dead code.

### Decisions made while building

- **Compiled bindings are enabled** (`AvaloniaUseCompiledBindingsByDefault`). Without this, a mistyped binding path is a silent runtime no-op — and the workspace panel only appears after a workspace is open, so a broken binding there would not show up in a smoke test. Now it is a build error.
- **No DI container.** Four objects, one window. `MainWindowViewModel` has a parameterless constructor for the designer and an injecting one for tests.
- **The view supplies file pickers and clipboard access as three `Func` properties**, set in `DataContextChanged`. The view model never references an Avalonia type, which is what lets it be tested from the existing test project instead of needing a second one.
- **Settings path is injectable** on the view model. Without it, running the tests would write to the developer's real `%AppData%`.
- **Workspaces are keyed by solution path when there is one**, so opening a folder and opening the solution inside it restore the same saved selections rather than creating two entries.
- **A saved context beats "first in the list".** On refresh, the previously selected context is reselected if it still exists; only a context that has genuinely disappeared falls back to the first. Silently moving the user to a different context is how a migration gets applied to the wrong database.
- **Preflight runs twice**: once at startup, then again per workspace with that workspace's directory. `dotnet ef` availability is directory-dependent when a local tool manifest is in play.
- **Preflight failure blocks discovery** rather than letting the command fail with a confusing message.
- **`AutoNoBuildFirst` retries with a real build** when the `--no-build` attempt fails, and says so in the status bar.
- **Every command is echoed into the console** as `> dotnet ef ...` before it runs, satisfying the plan's "visible verbatim" requirement.
- **The tooling probe runs on `Window.Opened`**, not in the constructor, so it does not delay first paint by a second.

### Verified

- **77 tests, all passing**, in about 3 seconds. New this phase:
  - `PreflightTests` — 6: both versions reported when installed, probes `dotnet ef` rather than the `dotnet-ef` executable (so a local tool manifest works), a missing tool is a problem with the SDK version still reported, a silent failure still produces a usable message, survives the SDK probe failing, and one test against the real environment.
  - `SettingsTests` — 1 added: discovery mode defaults to `Auto` and round-trips as a name, so reordering the enum cannot change a saved workspace's mode.
  - `MainWindowViewModelTests` — 14: auto-discovery on open, suggested projects preselected, Manual opens without building and Refresh still works, `AutoNoBuildFirst` retries with a build on failure and does *not* rebuild when the fast path works, selections survive a restart, refresh keeps the chosen context, a vanished context falls back to the first, folder and solution are the same workspace, a missing tool blocks discovery, the command is echoed verbatim, closing returns to the landing state, diagnostics carry environment and last command.
  - One of those 14 is a genuine end-to-end test — real `EfRunner`, real `dotnet ef`, the `samples/SampleEfApp` project — asserting the context dropdown populates with both contexts. This is the Phase 2 exit criteria, and it is the only test that would catch a wrong flag reaching the CLI.
- `dotnet build` clean, 0 warnings, including a full non-incremental rebuild with compiled bindings on.
- The app was launched from its built executable and reached a titled window with empty stderr.

### Not done in Phase 2

- **No visual/manual UI review.** Correctness of the bindings is enforced by the compiler and the exit criteria by the end-to-end test, but nobody has looked at the layout. Worth a pass before Phase 3 adds more to it.
- **Cancel is wired but unexercised.** The button, the token, and `Kill(entireProcessTree: true)` are all in place; verifying no orphaned MSBuild nodes survive still needs a slow real command to interrupt. Carried forward to Phase 3.
- **Window size and position are not persisted.** Not asked for.
- No migrations list, script, or database commands — those are Phases 3 and 4.

---

## Phase 2 review round 1

Manual UI testing raised seven points. All addressed.

### Bugs found and fixed

**The Skip build checkbox did nothing.** Reported: `--no-build` only had an effect in `AutoNoBuildFirst` mode, and only after closing and reopening the workspace.

Root cause: `DiscoverContextsAsync(bool noBuildFirst)` called `BuildTarget(forceNoBuild: noBuildFirst)`. The parameter is a `bool?` override where null means "use the checkbox", but a plain `bool` was being passed — so every mode except `AutoNoBuildFirst` passed `false` and silently overrode the user's choice. Fixed by passing `noBuildFirst ? true : null`, so the override only applies when a mode specifically demands it.

A second, related decision came out of the fix: the retry-with-a-build fallback now only fires when `AutoNoBuildFirst` was the one that chose `--no-build`. If the *user* ticked Skip build and the command fails, that failure is theirs to see, not something to quietly undo behind their back.

Covered by a `[Theory]` across all three discovering modes, plus a manual-refresh test that toggles the checkbox both ways, plus a test that the fallback does not override an explicit Skip build.

**Output collection was mutated off the UI thread.** Not reported — the test suite crashed the test host with `System.ArgumentException: Destination array was not long enough` inside `ObservableCollection.Add` once the new tests exercised streaming harder.

Root cause: `new Progress<OutputLine>(Append)` marshals through the `SynchronizationContext` captured at construction. In the app that is Avalonia's UI thread, so it happened to be correct; in tests there is no such context, so callbacks ran on thread-pool threads concurrently with the test thread. An ambient dependency that was invisible until something moved.

Fixed by making the marshalling explicit: a `PostToUiThread` hook on the view model, defaulting to `Dispatcher.UIThread.Post`, replaced with a direct invoke in tests. `Progress<T>` is replaced by a small `LineProgress` adapter that captures no context.

### Changes made

**Remembered contexts, and a new default.** The set of `DbContext` types in a solution rarely changes, so `WorkspaceSettings.KnownContexts` now stores the contexts found last time and the dropdown is populated from it on open — no build. `DiscoveryMode` gained a fourth value, `Cached`, which is the new default:

| Mode | On open |
| --- | --- |
| `Cached` (default) | Use the remembered list. Discover only if nothing is remembered yet. |
| `Auto` | Always re-discover. |
| `AutoNoBuildFirst` | Always re-discover, trying `--no-build` first. |
| `Manual` | Never discover, even with nothing remembered. Refresh only. |

`Manual` was kept as a distinct mode rather than folded into `Cached` for two reasons: it is still the right answer for a solution where even the first discovery is unwelcome, and keeping the name means no existing saved settings file becomes unreadable. That last point matters more than it looks — `Load` treats a `JsonException` as corruption and moves the file aside, so removing an enum name would have quietly reset every saved workspace.

`Cached` is deliberately the enum's zero value, so `default(DiscoveryMode)` agrees with the settings default. Without that the view model started on `Auto` and the dropdown briefly disagreed with the stored setting. Asserted in a test.

Note for existing settings files: workspaces already saved with `"Discovery": "Auto"` keep `Auto`. Only new workspaces get `Cached`.

**App-wide settings.** `AppSettings.Display` holds preferences that are the same wherever the app is pointed; `WrapOutput` is the first. It saves even with no workspace open, which `Persist()` deliberately does not do. No Settings dialog yet — options stay inline next to what they affect, and the dialog is on the roadmap.

**Output console word wrap.** A Wrap checkbox on the output toolbar, bound to the app-wide setting. Off means a horizontal scrollbar appears; on means it is disabled, since sideways scrolling is pointless once lines wrap and the bar only costs height. Both are driven by two one-line `FuncValueConverter`s in `OutputConverters.cs` rather than a converter class each.

**Selectable output text.** `TextBlock` → `SelectableTextBlock`, which keeps the per-channel colouring (errors red, warnings amber, build noise grey) while allowing any single line to be selected and copied. Cross-line dragging is not possible with per-line controls, so a **Copy all** button covers the multi-line case, alongside the existing Copy diagnostics.

**Recent workspace items.** Each entry is now a `RecentWorkspace(Name, Location, Path)` record instead of a bare string: solution name in semibold on top, containing folder underneath in smaller muted text, full path as a tooltip, taller button. A folder workspace uses the folder name, since there is no file name to take.

**Theme support** added to `ROADMAP.md` rather than built, as asked. The entry notes that the app already follows the OS light/dark setting via `RequestedThemeVariant="Default"`, and that the first real step is replacing the hard-coded hex colours with theme resources.

### Verified

- **88 tests passing.** 11 added: `Cached` is the default and discovers once when nothing is remembered; `Cached` reuses the remembered list and runs nothing; a remembered list fills the dropdown even in `Manual`; Skip build honoured in all three discovering modes (theory, 3 cases); Skip build honoured by a manual refresh in both positions; Skip build not silently undone when the no-build attempt fails; word wrap is app-wide and saves with no workspace open; Copy all copies every line; recent entries split into name and location.
- `dotnet build` clean, 0 warnings, with compiled bindings on — so the new `SelectableTextBlock`, converter and `RecentWorkspace` bindings all resolve at compile time.
- App relaunched from its built executable: reaches a titled window, empty stderr.

### Still outstanding from this round

- **Cross-line selection in the console is not possible.** Chosen trade-off: per-line selection keeps the channel colouring. Copy all covers the bulk case.
- The wrap toggle and the discovery/skip-build controls remain inline. A Settings dialog is on the roadmap.

---

## Phase 3 — Done

### Answers that shaped this phase

Four questions were put to the user before implementation:

1. **Layout** — tab control above, output console below, with a draggable `GridSplitter` between them, so a build can be watched while reading the list.
2. **Confirmations** — a hand-rolled reusable modal `ConfirmWindow`, no dialog dependency.
3. **Migration list refresh** — mirror the context-discovery choice: the same `DiscoveryMode` options (`Cached` / `Auto` / `Manual` / `AutoNoBuildFirst`) as a separate per-workspace setting.
4. **Offline fallback** — on a connection failure, retry with `--no-connect` automatically and flag it clearly.

### Built

Core:

| File | Contents |
| --- | --- |
| `MigrationName.cs` | Validates a migration name as a C# class name before it costs a build |
| `Settings.cs` | Added `MigrationRefresh`, `KnownMigrations`, `Offline` per workspace |

App:

| File | Contents |
| --- | --- |
| `ViewModels/CommandSession.cs` | Extracted from the shell: runs one command at a time, owns the console, cancel, copy, diagnostics |
| `ViewModels/MigrationsViewModel.cs` | The Migrations tab |
| `ViewModels/ConfirmRequest.cs` | A destructive action awaiting confirmation, with an optional type-this gate |
| `Views/ConfirmWindow.axaml(.cs)` | One reusable modal dialog for all three destructive paths |
| `Views/MainWindow.axaml` | Tabs above, splitter, console below; migration list, add/apply/destructive panels |
| `OutputConverters.cs` | Added state badge colour and label converters |

The shell view model was at 663 lines before this phase, which was the trigger recorded in the shortcut ledger for splitting it. `CommandSession` was extracted rather than passing the shell into the tab: the Phase 4 script tab needs exactly the same plumbing, and two consumers is what justifies the seam.

### Decisions made while building

- **The migration list reuses `DiscoveryMode` rather than defining a parallel enum.** The four values mean the same thing in both places, so a second enum would be duplicate vocabulary.
- **Remembered migrations never carry applied state.** `Store` strips `Applied` to null on the way out and `Restore` strips it again on the way in. A remembered row always reads **Unknown**, never Applied or Pending. This is the whole reason caching the list is safe: stale names are harmless, a stale "applied" is how a migration gets run against the wrong database.
- **The offline fallback does not match on error text.** On any failure of a connected list, it simply retries with `--no-connect`. If that succeeds, the build and model were fine and the database was the problem; if it also fails, the original error is reported. No brittle string matching against EF or provider messages.
- **`Unknown` state is treated as risky, not as pending.** A rollback target with unknown-state migrations after it still raises a confirmation, and the warning says the applied state is unknown. Guessing "pending" would silently skip the warning before a possibly destructive rollback.
- **Applying forward is not confirmed; anything that undoes applied work is.** Update-to-latest runs straight away — it is the ordinary action and the list already shows what is pending. Rolling back names every migration that would be reverted.
- **`Remove` always targets the last migration, not the selected row.** EF can only remove the most recent one, so offering it against a selection would be a lie.
- **Force-remove is off by default and its confirmation says what changes.** `migrations remove --force` reverts the migration in the database if it has been applied — a database change hiding behind a file operation.
- **A drop with no known database name is refused, not offered ungated.** If `dbcontext info` fails or yields no name, there is nothing to type, so the drop does not happen at all.
- **A missing confirmation handler fails closed.** With `ConfirmAsync` unset, every destructive command refuses to run rather than assuming consent.
- **The confirm dialog focuses Cancel** when there is no typed gate, so Enter and Space are the safe answer; when there is a gate, it focuses the text box and the confirm button starts disabled.
- **Opening a workspace now loads the migrations list too**, awaited, after context discovery settles. Without it the tab stayed empty until the context dropdown was touched.
- **`ConfirmRequest.IsSatisfiedBy` is case-sensitive and exact.** Case-insensitivity would let a near-miss destroy a database.

### Verified

- **145 tests passing**, about 40 seconds. 57 added:
  - `MigrationNameTests` — 8 (as a theory set): valid identifiers accepted, empty rejected, leading digit rejected, invalid characters rejected, C# keyword rejected, duplicate rejected case-insensitively, surrounding whitespace rejected rather than silently trimmed.
  - `ConfirmRequestTests` — 3 (one a theory of 7 cases): an ungated request is satisfied by anything, a gated one needs the exact value, and every near-miss — wrong case, empty, whitespace, prefix, suffix — leaves the gate shut.
  - `MigrationsViewModelTests` — 25: applied/pending parsed; offline reports Unknown not Pending; unreachable database falls back with a warning; a non-database failure reports the real error; refresh keeps the selection; refresh mode decides whether opening loads (theory over all four modes); cached mode runs nothing; a remembered list never claims Applied; applied state never written to settings; add passes the name and refreshes after; add passes an output directory; an invalid name blocks before a build; a duplicate name is rejected; remove confirms by naming the last migration; declining removes nothing; force only passed when ticked and the warning says what it does; no dialog means no action; update-to-latest needs no confirmation; rollback confirms and names what would be reverted; forward moves need no confirmation; unknown state still confirms; revert-all targets `0`; declining leaves the database alone; drop asks EF for the name and gates on typing it; SQLite is gated by file rather than the word `main`; declining drops nothing; no name means no drop offered; nothing runs before a project and context are chosen; every command carries the selected context.
  - `MigrationRoundTripTests` — 1, and it is the exit criteria.
- **Exit criteria met.** `MigrationRoundTripTests` drives the real `EfRunner` and the real `dotnet ef` against `samples/SampleEfApp`: opens the workspace, asserts the fixture state, adds a migration, applies it to the real SQLite database, asserts all three rows read Applied, removes it with force, asserts the files are gone — then restores the fixture in a `finally` and asserts the original state is back. Verified afterwards by hand that `samples/SampleEfApp` is untouched: two migrations, first applied, second pending.
- **Drop confirmation cannot be bypassed** by an empty or mismatched name: covered by `ConfirmRequestTests` for the gate itself and by two `MigrationsViewModelTests` for the paths that build it.
- `dotnet build` clean, 0 warnings, compiled bindings on — so every new binding in the tab, the list template and the dialog resolves at compile time.
- App launched from its built executable: titled window, empty stderr.

### Fixed while building

- **`TextBox.Watermark` is obsolete in Avalonia 12**; the build flagged it and it is now `PlaceholderText`.
- **A misleading doc comment on `ConfirmRequest.IsSatisfiedBy`** claimed an empty requirement was never satisfied. It is the opposite — an empty `RequiredTypedValue` means the request is not gated at all. Corrected, and the drop path already refused to build such a request.
- **Two test classes were driving `dotnet ef` against the same sample project in parallel**, which xunit does by default across classes. That produced a failure that looked like a product bug ("Could not read the migrations list") but was two builds racing over one project and one SQLite file. Both are now in a `SampleProjectCollection` with parallelisation disabled.

### Not done in Phase 3

- **No visual review of the new tab.** Bindings compile and behaviour is covered, but nobody has looked at the layout or the spacing of the three action panels.
- **Cancel is still not exercised against a long-running real command.** The plumbing is there and the round-trip test proves the commands work; interrupting one mid-build to confirm no orphaned MSBuild nodes survive is still a manual check.
- **Switching context reloads the list fire-and-forget** (`_ = Migrations.LoadForContextAsync()`), because a property setter cannot await. Fine in the UI, but it means that specific path is not covered by a test; the awaited path on workspace open is.
- No script generation — that is Phase 4, and the tab control has one tab until then.

---

## Phase 3 review round 1

Manual UI testing raised two points. Both done.

### Every database-touching action is now confirmed

Previously only destructive routes were confirmed: rolling back, reverting all, dropping, and
removing. Applying forward — Update to latest, and Update to selected where the target was ahead of
what was applied — ran immediately, on the reasoning that it was the ordinary action and the list
already showed what was pending.

**That reasoning was wrong, and the user overruled it.** A misclick on "Update to latest" runs
migrations against whatever database the startup project is currently pointed at, and there is no
undo. Being the ordinary action is exactly why the button is easy to hit by accident.

`NeedsDowngradeConfirmation` (which returned false for forward moves) became `BuildUpdateConfirmation`,
which always returns a request. The wording still distinguishes the two cases, because they are not
equally dangerous:

| Action | Title | Detail |
| --- | --- | --- |
| Apply forward | Apply migrations | Names the migrations that will be applied |
| Apply forward, nothing outstanding | Apply migrations | Says it should make no changes |
| Apply forward, offline | Apply migrations | Says some may already be in place, state unknown |
| Roll back | Revert migrations | Names what will be reverted and warns about data loss |
| Revert all | Revert all migrations | Warns the schema goes back to empty |
| Drop | Drop database | Type-the-name gate, unchanged |

Confirmations now also name the context, so the dialog says which database is about to change.

**`migrations add` remains unconfirmed** — it writes source files and touches no database. Pinned
down by a test so it does not drift.

### Sort order toggle

An icon-only button above the list flips between oldest-first (EF's own order, the order migrations
are applied in) and newest-first. The glyph shows the current direction (↑ / ↓) and the tooltip
explains what clicking does. Stored in `DisplaySettings.SortNewestFirst`, so it is app-wide like the
console wrap setting rather than per workspace.

**The bug this could easily have introduced:** `LastMigration` was `Migrations.LastOrDefault()`, and
Remove targets the last migration because that is the only one EF can remove. Had the displayed list
simply been reversed, flipping the sort would have silently pointed Remove — and the index arithmetic
behind every rollback warning — at the *oldest* migration.

So `MigrationsViewModel` now keeps `_ordered`, the chronological list as EF reports it, and
`Migrations` is only a view of it. `LastMigration`, `IndexOf`, the rollback calculation, the counts
and `Store` all read `_ordered`. Three tests cover it: the flip itself, that Remove still targets the
newest after flipping, and that remembered migrations are stored chronologically whatever the display
order.

### Verified

- **153 tests passing**, 8 added and 2 rewritten:
  - Rewritten: the two tests that asserted forward applies were unconfirmed now assert they are
    confirmed, with apply-flavoured rather than rollback-flavoured wording.
  - Added: declining a forward apply changes nothing; a forward apply with nothing outstanding says
    so; adding a migration is not confirmed; the list is oldest-first by default and can be flipped;
    sorting newest first does not change which migration Remove targets; sorting does not change a
    rollback warning; the sort order is remembered app-wide; remembered migrations are stored
    chronologically whatever the display order.
- `dotnet build` clean on a full non-incremental rebuild, 0 warnings, compiled bindings on.
- The end-to-end round trip still passes against the real database, and `samples/SampleEfApp` was
  confirmed unchanged afterwards.
- App relaunched from its built executable: titled window, empty stderr.

---

## Phase 4 — Done

### Answers that shaped this phase

Three questions were put to the user before implementation:

1. **Script files** — show a stable suggested name in an editable textbox, derived from the current
   choices, and confirm before overwriting an existing file.
2. **Idempotent gate** — probe the provider lazily, the first time the Script tab is opened, and cache it.
3. **Range** — presets (All / Pending) plus a Custom range with two pickers.

### Built

| File | Contents |
| --- | --- |
| `Core/ScriptFileName.cs` | Builds the suggested filename; sanitises path separators |
| `App/ViewModels/ScriptViewModel.cs` | The Script tab |
| `App/Views/MainWindow.axaml` | Script tab: range row, destination row, read-only SQL viewer |
| `App/Views/MainWindow.axaml.cs` | Save As picker, open-file and reveal-in-folder via the OS shell |

`MigrationsViewModel` gained one member: `Ordered`, the chronological list, which the Script tab uses
to build its range pickers.

### Decisions made while building

- **The suggested filename stops tracking the choices once the user types their own.** `ResetFileName`
  puts the suggestion back and re-arms the tracking. Silently rewriting a name someone deliberately
  typed would be worse than a slightly stale suggestion.
- **The overwrite confirmation only fires for the configured-folder route.** The OS Save As dialog
  does its own overwrite prompt, so asking again there would be two dialogs for one decision.
- **`Custom` with only a To selected sends `0` as the From.** EF takes these positionally, so a lone
  To would be read as a From and silently script a different range. The pickers make this hard to hit,
  but the translation guards it anyway.
- **`Pending` warns rather than refuses when the applied state is unknown.** Offline, "pending" is a
  guess — but a wrong script is a file, not a database change, so a visible warning is the right
  weight of response.
- **An unknown provider leaves the idempotent box enabled.** Same reasoning as
  `DbContextDetails.SupportsIdempotentScripts` from Phase 1: better to attempt and surface EF's own
  clear error than to grey out something that would have worked.
- **A provider that cannot do idempotent scripts also unticks the box**, so switching from SQL Server
  to SQLite cannot leave an unsupported flag armed.
- **The provider probe caches per context name** and only populates the cache on success, so a probe
  that failed (or was skipped because another command was running) retries on the next visit.
- **The viewer is a read-only `TextBox`**, not a per-line control like the console. Scripts are read
  and copied whole, so free selection matters more than per-line colouring.
- **Reveal-in-folder degrades off Windows.** `explorer.exe /select` is Windows-only, which matches the
  v1 publish target; elsewhere it opens the containing folder, which is the useful part. Marked with
  a `ponytail:` comment.
- **Generating a script is not confirmed.** It writes a file and touches no database — the same line
  drawn for `migrations add`. Overwriting an existing file *is* confirmed.

### Verified

- **193 tests passing**, 40 added:
  - `ScriptFileNameTests` — 7: names a full script, names a range, marks idempotent separately, is
    stable for the same choices, falls back without a context, cannot escape the target folder via
    path separators (theory of 4), trims very long names but keeps the extension.
  - `ScriptViewModelTests` — 30: All passes no range; Pending starts at the last applied migration;
    Pending with nothing applied starts at the beginning; Pending warns when applied state is
    unknown; Custom passes both endpoints; Custom with only an end point still sends a start; the
    pickers are built from the migrations list; a selection that no longer exists falls back; the
    provider is probed once per tab opening; SQLite cannot use idempotent and the tooltip says why;
    an unknown provider keeps the option; idempotent is passed only when ticked and supported; a
    configured folder is used without asking; Save As decides when no folder is set; cancelling
    generates nothing; overwriting in the configured folder is confirmed and declining preserves the
    file; accepting replaces it; the suggested name follows the choices; a hand-typed name survives
    later choices and Reset restores tracking; a name without an extension gets one; the SQL is read
    back byte for byte; open/reveal/copy only enable after a script exists; open and reveal hand over
    the path; a failed generation leaves the viewer alone; the folder round-trips through settings;
    an empty folder stores as null; nothing generates before a project and context are chosen.
  - `MainWindowViewModelTests` — 1 added: selecting the Script tab probes the provider.
  - `ScriptGenerationTests` — 2, and they are the exit criteria.
- **Exit criteria met.** `ScriptGenerationTests` runs the real CLI against `samples/SampleEfApp`:
  - *Matches the CLI* — generates through the app, then invokes `dotnet ef migrations script` again
    with **hand-written arguments rather than `EfArgs`**, and compares the two files byte for byte.
    An independent check, not a restatement of what the app already believes.
  - *Both destination modes* — once into a configured scripts folder, once through a Save As
    callback, both byte-identical to the reference.
  - *All three post-generation actions* — Open file, Open folder and Copy SQL all receive the right
    path and content.
  - A second test confirms the Pending range really does script only the outstanding migration, and
    that SQLite is correctly detected as unable to do idempotent scripts.
- `dotnet build` clean, 0 warnings, compiled bindings on.
- App relaunched: titled window, empty stderr. `samples/SampleEfApp` unchanged — script generation is
  read-only.

### Fixed while building

- **Two MVVM toolkit warnings (MVVMTK0034)**: `RefreshOptions` was writing the backing fields of
  `[ObservableProperty]` members directly, which skips change notification. Rewritten to use the
  generated properties.
- **A race in the first draft of the exit-criteria test**: setting `SelectedTabIndex` fires the
  provider probe fire-and-forget, and the test's own `await OnActivatedAsync()` then hit
  `IsRunning` and no-opped, leaving `ProviderDetails` null. The slow test now awaits activation
  directly and a separate fast test covers the `SelectedTabIndex` wiring. Worth noting the same
  shape exists in the app: visiting the Script tab while a command runs skips that probe, but it
  retries on the next visit because only successes are cached.

### Not done in Phase 4

- **No visual review of the Script tab.** Bindings compile and behaviour is covered, but nobody has
  looked at it.
- **`--no-transactions` is not offered.** EF supports it; nothing has asked for it.
- No syntax highlighting in the viewer — still on the roadmap as AvaloniaEdit.
- Cancel remains unexercised against a long real command, carried from Phase 2 and 3.

---

## Phase 4 review round 1

Manual UI testing raised two points. Both done.

### Row numbers on the migrations list

Each row now shows its 1-based position in EF's chronological order — the order migrations are
applied in — in a narrow first column.

The number belongs to the migration, not to the row position, so flipping the sort to newest-first
shows `3, 2, 1` rather than renumbering. The first migration is always 1 whether it is at the top or
the bottom.

This needed a display wrapper: `MigrationRow(int Index, MigrationInfo Info)` in the App layer, with
`Id`, `Name` and `State` delegating to the migration. `MigrationsViewModel.Migrations` is now a
collection of rows, numbered from `_ordered` *before* being ordered for display. `Ordered` itself is
untouched and still hands `MigrationInfo` to the Script tab.

Putting the index on `MigrationInfo` was the alternative and would have been wrong: that record is
the shape of EF's JSON, and a display concern has no business in it — it would also have been
persisted into `KnownMigrations`.

All 40 existing migrations-tab tests passed unchanged after the switch, because the wrapper exposes
the same members the tests use.

### A visible grab handle on the output splitter

The `GridSplitter` was an 8px transparent strip: functional but invisible, with nothing to aim at. It
now has a templated grip — a rounded 64×4 bar centred in a 12px band — plus a `SizeNorthSouth`
cursor, a hover state that brightens and thickens the grip, and a "Drag to resize the output panel"
tooltip.

### Verified

- **196 tests passing**, 3 added: rows are numbered from one in the order they are applied; row
  numbers stay with their migration when the sort flips; a remembered list is numbered too.
- `dotnet build` clean, 0 warnings, compiled bindings on — so the retyped `vm:MigrationRow` data
  template and the new splitter style both resolve at compile time.
- App relaunched: titled window, empty stderr.

---

## Phase 5 — Done

### Answers that shaped this phase

Three questions were put to the user before implementation:

1. **Where guidance appears** — a panel directly above the output console, not a dialog and not the
   preflight banner, so the raw output it explains is a glance away rather than a click.
2. **Actions** — text plus Copy diagnostics only. No auto-fix: every fix here is a user decision
   (pick a different startup project, add a design-time factory, correct a connection string).
3. **Stale data** — mark and keep, never clear. A list that could not be refreshed is still worth
   reading; what must not survive is the impression that it is current.

### Built

| File | Contents |
| --- | --- |
| `Core/EfDiagnosis.cs` | The `EfDiagnosis` record and the failure-to-guidance mapping |
| `App/ViewModels/CommandSession.cs` | Holds the diagnosis, guards against a throwing runner, includes the diagnosis in the copied block |
| `App/ViewModels/MigrationsViewModel.cs` | `IsStale` / `ShowsStaleWarning` |
| `App/ViewModels/ScriptViewModel.cs` | `IsStale` / `ShowsStaleWarning` for the SQL viewer |
| `App/ViewModels/MainWindowViewModel.cs` | `ContextsStale` / `ShowsStaleContexts` |
| `App/OutputConverters.cs` | Red-for-failure, amber-for-warning brushes for the guidance panel |
| `App/Views/MainWindow.axaml` | Guidance panel above the console; stale markers on the context list, the migrations list and the SQL viewer |

### Decisions worth recording

- **Matching is on `ErrorMessage`, not the whole output.** The first draft scanned every non-`data:`
  line, which would have matched phrases buried in a stack trace or a project name — a restore line
  mentioning a project called `Contoso.BuildFailed.Tests` was enough to report "the project did not
  compile". EF puts the real message on the `error:` lines, with an MSBuild fallback to unprefixed
  output, which is exactly what `EfResult.ErrorMessage` already returns.
- **A connection failure is matched before the wrapper it arrives in.** An unreachable database
  surfaces as `Unable to create a 'DbContext' ... The exception 'Login failed for user ...' was
  thrown`. Both rules match it; reporting the wrapper would send the user to look at design-time
  factories for what is a connection-string problem, so the connection rule is tested first.
- **The tool/runtime version gap is a warning, not a failure.** Reproducing it — an EF 8 tool
  against an EF 10 project — showed that EF warns and carries on, and returns a *wrong* answer: the
  migrations list came back with applied state the EF 10 tool disagrees with. A failure-only check
  would say nothing at the exact moment the user is being misled, so this one is checked on every
  command whatever the exit code, and renders amber rather than red.
- **Text scraping, deliberately.** This is the one place the plan's "never scrape EF output" rule is
  broken, and it is tolerable because nothing depends on it: an unmatched failure still shows EF's
  own message, the console and the diagnostics block. `EfRunner` already pins
  `DOTNET_CLI_UI_LANGUAGE=en`, so the phrases are stable. A wrong match costs a misleading
  paragraph, not a broken feature.
- **Stale is marked before a mutation runs, not after it fails.** `migrations add`, `remove`,
  `database update` and `database drop` set the flag before invoking, so a cancel or a crash halfway
  through leaves the list marked too — only a successful reload clears it. One line per command
  rather than a branch on every failure path.
- **The stale badge only appears when there is something to distrust.** `ShowsStaleWarning` is
  `IsStale && HasMigrations`: telling the user that an empty list may be out of date is noise.

### Reproduced deliberately

The exit criterion for this phase. Each mapped error was provoked against a real project and its
output captured into `tests/EfMigrateHub.Core.Tests/Fixtures/`, so the tests assert against EF's
actual wording rather than wording invented here. The recipes, for when EF changes its messages:

| Fixture | How it was provoked |
| --- | --- |
| `error-no-project-found` | `dotnet ef migrations list` in an empty directory |
| `error-no-project` | `--project ./nope.csproj`, a path with no project |
| `error-no-dbcontext` | A plain `dotnet new console` with no `Microsoft.EntityFrameworkCore.Design` reference (captured in Phase 0) |
| `error-build-failed` | A copy of `samples/SampleEfApp` with `this is not csharp;` appended to `Program.cs` |
| `error-dbcontext-create` | `BlogContext` given a `string` constructor parameter and no design-time factory |
| `error-connection` | `database update --connection "Data Source=/nope/nowhere/x.db"` — SQLite Error 14 |
| `error-migrations-assembly` | A `Data` classlib whose context sets `MigrationsAssembly("App")`, invoked with `--project Data` |
| `warn-tools-version` | A local tool manifest pinning `dotnet-ef` 8.0.0, run against the EF 10 sample |

Two mapped cases could **not** be reproduced on this machine, and are matched on EF's documented
wording alone:

- **`dotnet ef` missing entirely** — the global tool is installed here, and a local manifest without
  it still falls back to the global one. Uninstalling the tool to prove a string match was not worth
  it; `Preflight` already covers the same ground at startup with a real check rather than a match.
- **SQL Server and PostgreSQL connection failures** — the sample uses SQLite, which fails with
  `unable to open database file` (reproduced) rather than `Login failed for user` or `A
  network-related or instance-specific error` (not reproduced). All three map to the same guidance.

### Verified

- **223 tests passing**, 27 added:
  - `EfDiagnosticsTests` — 13: each of the seven captured failures maps to the right guidance; a
    version gap is reported even though the command succeeded; a clean success and an unrecognised
    failure both say nothing; a phrase buried in the build log does not match; a connection failure
    inside a `DbContext` wrapper reports the connection; an MSBuild error on unprefixed output still
    matches.
  - `CommandSessionTests` — 7: a recognised failure is explained; the next success takes the
    explanation away; dismissing it leaves the output alone; Copy diagnostics produces one block
    holding environment, diagnosis, command, directory, exit code and full output; copying before
    anything has run says so rather than copying nothing; a runner that throws still stops the
    spinner and reports why; a failed command leaves the status bar reporting the failure.
  - `MigrationsViewModelTests` — 5 added: a failed refresh keeps the list and marks it; a successful
    refresh clears the mark; a failed `database update` and a failed `migrations add` both leave it
    marked; an empty list shows no badge.
  - `ScriptViewModelTests` — 1 added: a failed generation keeps the previous SQL and marks it, and a
    later success clears it.
  - `MainWindowViewModelTests` — 1 added: a failed context refresh keeps the contexts, marks them,
    and explains the failure.
- `dotnet build` clean, 0 warnings, compiled bindings on — so the new converters and the
  `Session.Diagnosis.IsWarning` bindings resolve at compile time.
- `samples/SampleEfApp` untouched: every reproduction ran against copies in the temp directory.

### Not done in Phase 5

- **No visual review of the guidance panel.** Bindings compile and the behaviour is covered, but
  nobody has looked at it, and the amber/red pair has not been checked against the light theme.
- The two unreproducible cases above are matched on documented wording, not captured output.
- Cancel remains unexercised against a long real command, carried from Phases 2, 3 and 4.

---

## Theme support (roadmap item, light/dark/system)

- Every hard-coded hex is gone from the views. `App.axaml` now holds one brush per semantic role under
  `ResourceDictionary.ThemeDictionaries`, with a separately tuned light and dark value, read through
  `DynamicResource`. `MainWindow.axaml` and `ConfirmWindow.axaml` contain no colour literals at all.
- The three colour-producing converters (`OutputChannelBrushConverter`, `OutputConverters.StateBadge`,
  `DiagnosisBackground`/`DiagnosisBorder`) are replaced by bool converters driving style classes.
  A converter returning a `SolidColorBrush` does not re-run on a theme change, so those would have
  left stale colours on screen after a switch — that was the actual reason to remove them, not tidiness.
- Toolbar dropdown: System / Light / Dark, persisted app-wide in `DisplaySettings.Theme`. System maps
  to `ThemeVariant.Default`, so the OS-following behaviour that was the old default is still reachable
  and is still what a fresh install gets.
- `App.ApplyTheme` is called before the main window is constructed, so a dark-theme user gets no white
  flash on startup. It no-ops when `Application.Current` is null, which is the case in tests and in the
  XAML previewer.
- Light-variant values are tuned, not shared: the ambers and reds that read on a dark background are
  too pale on white, so `#D13C3C` became `#B3261E` in light and `#E86C6C` in dark, and the diagnosis
  panel's dark washes became genuine light tints rather than near-black on white.
- Tests: 228 pass, `dotnet build` clean with 0 warnings and compiled bindings on, so the new
  `Classes.*` bindings and every `DynamicResource` key resolve at compile time.

### Not done

- **No visual review in either variant.** The app launches and the bindings compile, but nobody has
  looked at the light theme on screen. The specific light values above are reasoned, not eyeballed —
  they are the first thing to adjust if something reads badly.
- Accent colour, font size and custom colour schemes are still roadmap items, not implemented.

---

## Syntax-highlighted SQL viewer (roadmap item)

### Answers that shaped this
- Colours: a tuned `.xshd` per theme variant, swapped on a theme change — not the bundled `TSQL`
  definition as-is, and not `AvaloniaEdit.TextMate`.
- Scope: the Script tab only. The output console keeps its per-line `ItemsControl` and channel
  colouring; there is no SQL in it to highlight.
- Features: line numbers, Ctrl+F search, and a wrap toggle. Code folding was declined.
- The wrap preference persists app-wide in `DisplaySettings`.

### Built
- `Avalonia.AvaloniaEdit` 12.0.0, which declares `Avalonia` 12.0.0 against the 12.1.1 already
  referenced, so no version pinning was needed. `App.axaml` includes
  `avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml` — without it the control has no template
  and renders as an empty box.
- `Highlighting/Sql-Light.xshd` and `Highlighting/Sql-Dark.xshd`, embedded resources read by
  `SqlHighlighting.For(ThemeVariant)`, which parses each once and caches it.
- The read-only `TextBox` in the Script tab is now `ae:TextEditor` with `IsReadOnly`,
  `ShowLineNumbers`, and `WordWrap` bound to the new `MainWindowViewModel.WrapSql`.
- `SearchPanel.Install(SqlEditor)` in the window constructor: Ctrl+F, F3, Ctrl+H. Replace does
  nothing on a read-only document, but search is the point for a long script.
- A Wrap checkbox and a "Ctrl+F to search" hint beside the existing Copy / Open file / Open folder
  buttons, so the search affordance is discoverable rather than folklore.
- `DisplaySettings.WrapSql`, saved through the same direct `SettingsStore.Save` path as `WrapOutput`.

### Decisions worth recording
- **Two definition files, not one.** A `HighlightingColor` holds a literal colour, not a theme
  resource, so nothing repaints it when the variant changes — the same trap that got the
  brush-returning converters deleted during the theme work. The only way a theme switch can recolour
  the SQL is to hand the editor a different definition, so there is a file per variant and
  `ActualThemeVariantChanged` swaps between them. That event was chosen over a handler on the `Theme`
  property because it also fires for a System user whose OS flips while the app is running.
- **The keyword list is upstream's, verbatim.** Both files are derived from AvaloniaEdit's bundled
  `TSQL-Mode.xshd`. Only the five colours and two added rules differ between them; the ~580 keywords
  are inert duplication that no one has to maintain, which is cheaper than hand-curating a shorter
  list and then discovering what it missed.
- **A quoted-identifier rule was added, because the bundled definition has none.** Its only string
  rule spans `'`, so PostgreSQL's and SQLite's `"Blogs"` would have coloured as an unterminated
  string literal and dragged the rest of the line with it. `[dbo].[Blogs]` and `"Blogs"` now both
  read as identifiers. A number rule was added for the same reason: EF's scripts are full of lengths
  and version strings, and unhighlighted digits were the biggest remaining wall of one colour.
- **`WrapSql` lives on `MainWindowViewModel`, not `ScriptViewModel`.** It is app-wide, and
  `MainWindowViewModel.Persist` deliberately bails out when no workspace is open — putting it on the
  Script view model would have meant the toggle silently failing to save in exactly that state.
  Sitting next to `WrapOutput` it reuses a path that is already correct.
- **The document is assigned from code-behind.** `TextEditor.Text` is a plain CLR property, not a
  styled one, so it cannot be bound; `Document` can be, but only to a `TextDocument`, which would put
  an AvaloniaEdit type in the view model. A `PropertyChanged` handler on `ScriptViewModel.Sql` that
  builds a fresh `TextDocument` is smaller than either alternative, and a new document resets the
  caret, selection and undo stack so the previous script's state does not carry over.
- **The definitions are not registered with `HighlightingManager`.** Nothing looks them up by name or
  by extension, and registering both would mean two definitions claiming `.sql`.
- Wrap defaults to off. EF's generated SQL is indented meaningfully and wrapping breaks that up.

### Verified
- 249 tests pass, up from 228. The 21 new ones are `SqlHighlightingTests`, which matter more than
  their size suggests: the definitions are embedded resources loaded by name and parsed at runtime,
  so a typo in a file or in the resource path would otherwise be a crash on first sight of the Script
  tab rather than a build error.
- The tests drive a real `DocumentHighlighter` over sample fragments, so they assert the definition
  actually colours `SELECT`, `select`, `-- comment`, `'literal'`, `[dbo]`, `"Blogs"`, `42`, `3.14`
  and `GO`, not merely that the file parses. They also assert light and dark share no colour value,
  which is the failure a copy-paste between the two files would produce and which nothing on screen
  would catch, since both variants would still highlight.
- `dotnet build` clean, 0 warnings, compiled bindings on — so the new `WrapSql` binding and the
  `ae:TextEditor` markup resolve at compile time.
- The app launches with the new `StyleInclude` and `SearchPanel.Install` in the constructor, so
  neither throws during window construction.

### Not done
- **No visual review of the viewer.** Nobody has generated a script and looked at it in either
  variant. The colours are VS Code's Light+ and Dark+ values, which are at least proven against
  those backgrounds, but the line-number gutter and the search panel inherit AvaloniaEdit's Fluent
  styling and have not been checked against the app's own palette.
- Code folding is not implemented and stays on the roadmap.
- The `Identifier` rule treats `"` as always opening an identifier. A provider that uses `"` for
  string literals — which is standard SQL, though no EF provider generates it — would colour those
  as identifiers. Wrong colour, not wrong parse.

---

## View a migration's Up/Down changes (roadmap item)

The Migrations tab now has a detail pane beside the list: the selected migration's `.cs` file, and
on request the SQL that migration alone would run.

### Answers that shaped this
- **Where?** To the right of the list, with a draggable divider — the selection and what it means
  stay on screen together.
- **Where does the SQL come from?** `dotnet ef migrations script <previous> <this>` into a temp file,
  cached by migration id for the session. `migrations script` can only write to a file, so viewing
  one requires producing one.
- **How much of the `.cs`?** The whole file, read-only. Up and Down are adjacent and nothing has to
  be parsed to find them.
- **When is the SQL generated?** On an explicit button, never on selection — it builds.

### Built
- `MigrationFiles` in Core: finds the `.cs` for a migration id, and decides the temp path for a
  generated preview. No package references added; it is `Directory`/`Path`/`SHA256`.
- `MigrationDetailViewModel`: source, SQL, which of the two is showing, and the commands. Owned by
  `MigrationsViewModel`, which calls `Show` on every selection change.
- `Highlighting/CSharp-Light.xshd` and `CSharp-Dark.xshd`, and `SqlHighlighting` generalised into
  `SyntaxHighlighting` with `Sql(variant)` and `CSharp(variant)`.
- Migrations tab restructured into `list | GridSplitter | detail`, with a `Source` / `SQL` /
  `Copy` / `Open file` toolbar over one `ae:TextEditor`.

### Decisions worth recording
- **The file is found by convention, not asked for.** `migrations list --json` returns an id and a
  name but no path, and no CLI command reports where a migration file lives — `--output-dir` is an
  input only. So the migrations project is searched for `<id>.cs`, skipping `bin`, `obj`, `.git` and
  `node_modules`. Skipping build output is not an optimisation: a stale copy of the source sits in
  `obj` after any build, and showing that as the migration would be a silently wrong answer.
- **The `.Designer.cs` sibling is not offered.** It holds the model snapshot rather than Up and
  Down, and its name does not match the search, so it is excluded for free.
- **SQL is behind a button and the source is not.** Reading a file is free; `migrations script`
  builds the project. Arrowing down a twenty-migration list must not queue twenty builds.
- **The cache is keyed by migration id and dropped whenever the list reloads.** That is the moment
  migration files may have been added, removed or edited, so anything generated from them stops
  being trustworthy. A failed generation is never cached.
- **Switching selection drops out of the SQL view unless the new migration's SQL is already
  cached.** Otherwise the pane would keep showing one migration's SQL under another's name.
- **The temp path is keyed by a hash of project and context.** Two contexts in one solution can have
  identically named migrations, and reading one context's SQL under the other's name is exactly the
  wrong-answer class this feature exists to avoid.
- **One editor for both languages, not two.** The search panel, the wrap setting and the scroll
  behaviour are then identical in both views, and the definition is swapped alongside the document.
  The C# definitions are per-variant for the same reason the SQL ones are: a `HighlightingColor`
  holds a literal colour, so a theme switch can only be answered by loading a different definition.
- **The C# definition is deliberately smaller than AvaloniaEdit's bundled one.** Migration files are
  generated code with a narrow vocabulary; comments, strings, keywords, built-in types and numbers
  cover them. Verbatim strings needed care: `@"..."` doubles its quotes, so the escape is consumed
  by an inner rule set rather than by a lookahead on the end pattern — the lookahead version ends
  the string on the second quote of the pair, which the test caught.

### Verified
- `dotnet test EfMigrateHub.slnx` → **299 passed, 0 failed, 0 warnings** (was 249).
- `MigrationFilesTests` covers the conventional folder, a custom `--output-dir` location, the
  Designer file being ignored, a stale `obj` copy losing to the real source, and a stale copy alone
  yielding nothing rather than a wrong answer.
- `MigrationDetailViewModelTests` drives a fake runner that writes to the `--output` path it is
  given, so the cache, the "0"-for-the-first-migration range, the previous-migration range, the
  temp destination and the never-show-the-previous-migration's-SQL rule are all asserted against
  the real argument list.
- `SyntaxHighlightingTests` runs a real `DocumentHighlighter` over C# fragments, including the
  doubled-quote verbatim string.
- `dotnet build` clean with compiled bindings on, so the new detail-pane bindings resolve at compile
  time. The app launches with the second editor and its `SearchPanel.Install` in the constructor.

### Not done
- **No visual review of the pane.** Nobody has opened a real workspace, selected a migration and
  looked at it in either theme variant.
- **The splitter position is not remembered between sessions.** The columns are proportional
  (`2*` / `3*`), so it comes back sensible at any window size, but a dragged width is lost on close.
- **No Up/Down split and no diff between two migrations.** The whole file is shown; Up and Down are
  found by reading. A structured diff stays out of scope — see the roadmap's "Not planned".
- **Editing a migration file mid-session does not invalidate its cached SQL.** Refreshing the
  migrations list does. There is no file watcher.

### Idempotent option moved to the shared workspace pane
The migration detail pane's SQL preview initially ignored the idempotent flag entirely — `migrations
script` was always called without `--idempotent`. Fixed by sharing the option rather than adding a
second one.

- **One flag, not two.** `Idempotent` moved off `ScriptViewModel` and onto `MainWindowViewModel`,
  persisted per workspace next to `NoBuild` and `Offline`, and shown as a checkbox in the same
  left-hand options pane. Both the Script tab and the detail pane's `SQL` button read it through a
  `Func<bool>`, so there is nowhere for the two to disagree.
- **The provider gate is shared too, not duplicated.** `CanUseIdempotent`/`IdempotentTooltip`/the
  provider probe stay on `ScriptViewModel` — it is where the probe has always run, lazily, on first
  visit to the Script tab. `MigrationDetailViewModel` reads `Script.CanUseIdempotent` and calls
  `Script.EnsureProviderKnownAsync()` before its own first generation, rather than probing a second
  time for a capability that depends only on the context. `MainWindowViewModel` constructs `Script`
  before `Migrations` for this reason — a code comment marks why the order matters.
- **The SQL cache is keyed by migration id and the idempotent flag together.** Flipping the option
  and pressing `SQL` again must not serve back the other variant's result; flipping it back finds
  the earlier result still cached rather than rebuilding a third time.
- **Unticking on an unsupported provider is now a callback, not a direct property write.**
  `ScriptViewModel` no longer owns the flag, so it cannot null it itself; it calls
  `onIdempotentUnsupported`, which the shell wires to `Idempotent = false`.

Verified: `dotnet test` → **303 passed, 0 failed** (was 299). `ScriptViewModelTests` updated for the
new constructor shape; `MigrationDetailViewModelTests` gained cases for the ticked-and-supported,
ticked-and-unsupported, provider-probed-first-generation, and separately-cached-by-flag behaviour.
Not done: no visual check that the shared checkbox's enabled/disabled state updates promptly when
switching context on the Migrations tab without ever having visited the Script tab.

---

## Phase 6 — Done

### Built

Phase 6 in `PLAN.md` was a single-file publish and nothing else. The brief for it added "ideally the
published installer will also be able to handle updating the app", which pulls two items forward
from the roadmap: installer packaging, and auto-update. Both are now in.

**Velopack, not a hand-rolled updater.** One package (`Velopack` 1.2.0) plus one CLI (`vpk` 1.2.0,
pinned in `.config/dotnet-tools.json`) covers the installer, the portable zip, delta packages, the
GitHub Releases feed, the download, and the restart into the new version. The alternative considered
was Inno Setup plus a hand-written check against the GitHub API — more code, no deltas, and the
restart dance is the part that is easy to get subtly wrong.

- `src/EfMigrateHub.App/Updates/IAppUpdater.cs` — the one seam over Velopack: `CurrentVersion`,
  `CanUpdate`, `CheckAsync`, `ApplyAndRestartAsync`. Same rationale as `IEfRunner`: tests need to
  produce "an update is available" without a GitHub release and without an installed application.
- `src/EfMigrateHub.App/Updates/VelopackUpdater.cs` — the real implementation, against
  `GithubSource("https://github.com/matthewtoghill/EfMigrateHub")`, stable releases only. The
  `UpdateManager` is built **lazily and inside a try/catch**: its constructor throws
  `"No VelopackLocator has been set"` unless `VelopackApp.Build().Run()` has run, which is true of
  the shipped app but not of the test host or the XAML previewer, both of which construct the shell
  view model directly. A null manager means `CanUpdate` is false, which is the correct answer for a
  development or portable run anyway.
- `src/EfMigrateHub.App/ViewModels/UpdateViewModel.cs` — states (`Idle`, `Checking`, `UpToDate`,
  `Available`, `Downloading`, `Failed`), the `Check` / `UpdateNow` / `Dismiss` commands, and the
  messages. Owned by `MainWindowViewModel` as `Update`, because it belongs to no workspace.
- `Program.Main` calls `VelopackApp.Build().Run()` as its first statement, before Avalonia. Velopack
  drives its install, update and uninstall hooks by relaunching the exe with a hook argument, and
  `Run()` exits the process in those cases; anything before it would run during an install too.
  `vpk pack` verifies this call is present and fails the build if it is not.
- `build/release.ps1` — the whole release: `dotnet test`, then a self-contained `dotnet publish`,
  then `vpk pack` into `releases/`, then optionally `vpk upload github`. `artifacts/` and
  `releases/` are gitignored.

**Two checks, one code path.** A silent check fires a moment after launch from
`App.OnFrameworkInitializationCompleted`, alongside the tooling preflight and deliberately not
awaited — an update check must never delay first paint, and it must never surface a failure. If it
finds nothing, or cannot reach GitHub, it says nothing at all. The manual "Check for updates" button
on the home page runs the same check but reports every outcome, including "up to date" and the
failure reason. The button stays visible but disabled on a development or portable run, because a
missing button reads as a missing feature.

**The banner is a `Border.caution`, above everything, dismissible.** It reuses the existing semantic
style rather than introducing a colour. "Update and restart" downloads and restarts; "Later" hides
it for the session without forgetting the update — `UpdateNowCommand` stays enabled, so the home
page still offers it. The next launch checks again.

**`PublishSingleFile` is off.** `PLAN.md` asked for it, and Velopack makes it the wrong call:
Velopack packs a directory and produces binary deltas against the previous release, so a single
bundled exe would mean re-downloading the whole ~50 MB application on every update. `PublishTrimmed`
stays off for the reason the plan gives.

**The version lives in one place.** `<Version>` in `EfMigrateHub.App.csproj`. `release.ps1` reads it
when `-Version` is not passed, and passes it to both `dotnet publish` and `vpk pack`.

### Verified

- `dotnet test EfMigrateHub.slnx` → **314 passed, 0 failed** (was 303). Eleven new
  `UpdateViewModelTests` against a fake `IAppUpdater`: up to date, update found, check failed,
  startup check silent on failure and on no update, uninstalled build never checks, dismiss hides
  the banner without forgetting the update, apply is called, and a failed download reports rather
  than leaving the banner claiming progress.
- `pwsh build/release.ps1` runs clean end to end and produces, in `releases/`:
  `EfMigrateHub-win-Setup.exe` (56 MB), `EfMigrateHub-win-Portable.zip` (51 MB),
  `EfMigrateHub-1.0.0-full.nupkg`, and the `releases.win.json` feed. `vpk` logged
  `Verified VelopackApp.Run() in 'System.Void EfMigrateHub.App.Program::Main(System.String)'`.
- The published self-contained build launches and shows its window when run directly from
  `artifacts/publish/win-x64/EfMigrateHub.exe`.
- `EfMigrateHub-win-Setup.exe --silent` installs per-user into `%LocalAppData%\EfMigrateHub`
  (`current/`, `packages/`, an `EfMigrateHub.exe` stub, `Update.exe`), exit code 0, and the
  installed app launches and shows its window. The root `EfMigrateHub.exe` is a stub that starts
  `current\EfMigrateHub.exe` and exits immediately — "exit code 0 straight away" from the stub is
  correct behaviour, not a crash.

### Not done

- **The download and restart path has never run for real.** There is no published GitHub release to
  update from or to, and this machine has neither `gh` nor a `GITHUB_TOKEN`, so
  `release.ps1 -Upload` could not be exercised. The check, the banner and the failure handling are
  covered by unit tests against a fake; Velopack's own download and apply are not. Publishing a
  v1.0.0 and then a v1.0.1 and watching a real install update itself is the outstanding test.
- **Nothing is signed.** SmartScreen will warn on first run of the installer until download
  reputation builds. `vpk pack --signParams` is where a certificate would go.
- **Windows only.** `release.ps1` hard-codes `win-x64`, as agreed in `PLAN.md` section 3 Q9.
  Velopack supports macOS and Linux; adding them is a RID list and a test run on each, and stays on
  the roadmap.
- **No update channel other than stable.** `GithubSource` is constructed with `prerelease: false`,
  so a pre-release on GitHub is invisible to the app, and there is no UI for opting in.
- **The banner has had no visual review in either theme.** It reuses `Border.caution`, which has,
  but not in this position or at this width.
- **Release notes are not wired up.** `vpk pack --releaseNotes` takes a markdown file; nothing
  generates one, and the banner shows only the version number.

---

## Deliberate shortcuts

Tracked so they do not rot into "later means never". Each is marked with a `ponytail:` comment at the site.

| Where | Shortcut | Upgrade when |
| --- | --- | --- |
| `EfRunner.RunAsync` | One lock around the output list | Output volume ever gets large enough to matter; a few hundred lines per command does not |
| `Workspace.GuessStartupProject` | Substring match on the raw project file instead of MSBuild evaluation | False positives appear. Also deliberately catches central package management, which an XML `PackageReference` scan would miss |
| `samples/SampleEfApp/Program.cs` | Relative connection string, so it must be run from its own directory | It causes confusion during manual testing |
| `tests/EfMigrateHub.Core.Tests` | Also hosts the App view model tests, rather than a separate `EfMigrateHub.App.Tests` project | The App grows enough test surface that mixing them gets confusing |
| `MainWindowViewModel` | One view model for the shell; tabs are separate | Resolved in Phase 3 — `CommandSession` and `MigrationsViewModel` split out |
| `MainWindowViewModel.PostToUiThread` | Public settable hook rather than an injected dispatcher abstraction | A third UI-thread concern appears; two (this and the picker delegates) is not a pattern yet |
| `MigrationsViewModel` | Reuses `DiscoveryMode` for the migration list instead of its own enum | The two ever need different options |
| `MainWindowViewModel.OnSelectedContextChanged` | Fire-and-forget list reload, so it is untested | It misbehaves in practice, or an awaitable seam appears |
| `MainWindow.axaml.cs` reveal-in-folder | `explorer.exe /select` on Windows, plain folder open elsewhere | Non-Windows becomes a publish target |
| `ScriptViewModel` provider probe | Skipped silently if a command is already running; retries next visit | It proves confusing in practice |
| `Highlighting/Sql-*.xshd` | The ~580-keyword list is duplicated across both variant files | Never, most likely — it is machine-generated and inert. Revisit only if the two files start diverging in rules as well as colours |
| `MainWindow.axaml.cs` SQL document | Rebuilt from scratch on every `Sql` change rather than diffed into the existing document | Scripts get large enough that reallocating the document is visible; a few thousand lines is not |
| `MigrationFiles.FindSource` | Walks the migrations project for `<id>.cs` on every selection, with no cache | A project is big enough that the walk is noticeable; it is a directory scan against a warm OS cache |
| `MigrationDetailViewModel` | Cached SQL is invalidated by a list refresh, not by watching the migration files | Editing a migration and re-reading its SQL without refreshing proves confusing |
| Migrations tab splitter | Column widths are proportional and not persisted | Someone resizes it every session |
| `VelopackUpdater` | A failure constructing the `UpdateManager` is swallowed, so "no updater here" and "broken updater" look the same to the UI | A user reports the update button doing nothing on an installed build |
| `UpdateViewModel` | `CanUpdate` is read once and never raises a change notification | It could change while the app is running, which an install cannot |
