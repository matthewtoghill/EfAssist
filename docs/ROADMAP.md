# EfAssist — Roadmap

Ideas deliberately cut from v1. Nothing here is rejected — it's parked with a reason, so the decision doesn't have to be re-argued from scratch.

See `PLAN.md` for the v1 scope and the decisions that put these items here.

---

## Cut from v1, with a known trigger to revisit

### Connection-string override (`--connection`)
Let the user point a migration run at a different environment from the UI, rather than relying on the startup project's config and user-secrets.

- **Why parked:** powerful and dangerous in equal measure. "Update database" against a connection typed into a text box is one mis-paste away from migrating production.
- **Revisit when:** there's a concrete workflow that needs it, and we can design the guard rails (named connection profiles with an explicit environment label, extra confirmation for anything not marked local).
- **Cost:** small in code, most of the work is in the safety design.

### Direct `__EFMigrationsHistory` querying
Query the history table directly with `Microsoft.Data.SqlClient` / `Npgsql` / `MySqlConnector` instead of relying on `dotnet ef migrations list --json`.

- **Why parked:** `--json` already returns a populated `applied` flag per migration. The history table stores only `MigrationId` and `ProductVersion` — no timestamps — so direct querying adds `ProductVersion` and nothing else, in exchange for three database drivers, connection-string discovery, and provider detection. See `PLAN.md` §2.3.
- **Revisit when:** either the Phase 0 spike shows `applied` is unreliable (in which case this becomes v1 work, not roadmap work), or a real need appears for per-provider detail that the CLI won't give us.
- **Cost:** ~1 day plus three NuGet packages.

### macOS and Linux publish targets
v1 publishes `win-x64` only.

- **Why parked:** Windows is the only platform needed right now, and a publish matrix is CI work with no user-facing payoff yet.
- **Revisit when:** someone needs to run it on macOS or Linux.
- **Cost:** small. Avalonia and the whole codebase are already cross-platform; this is adding RIDs to `build/release.ps1` and testing the process-kill behaviour on each OS. Velopack packs macOS and Linux from the same command, so the packaging half is already paid for.

### Installer packaging — Windows done, the rest still parked, signing outstanding
Windows installer and in-app auto-update: **done** in Phase 6.

- **Current state:** `build/release.ps1` runs a self-contained publish and hands the directory to Velopack (`vpk pack`), which produces `EfAssist-win-Setup.exe`, a portable zip, and a delta package against the previous release. `vpk upload github` publishes them to GitHub Releases, which is the feed the app reads. In the app, `IAppUpdater`/`VelopackUpdater` back an `UpdateViewModel`: a silent check shortly after launch, a manual "Check for updates" button in the settings modal, a dismissible app-wide banner offering "Update and restart", and an "Update now" button on the home page once that banner has been dismissed. See `PROGRESS.md`.
- **Not done:** code signing. Nothing produced is signed, so SmartScreen warns on first run until download reputation builds. `vpk pack --signParams` (or `--azureTrustedSignFile`) is the hook; the work is buying and handling a certificate, not the wiring. MSIX, DMG/pkg and AppImage/deb/rpm remain parked with the platforms themselves.
- **Revisit when:** the SmartScreen warning becomes a real obstacle to someone installing it, or macOS/Linux ship.
- **Cost:** signing is a certificate purchase plus an afternoon. Notarisation on macOS is its own day.

### Theme support — done
Light and dark, follow-the-OS, alternative palettes, configurable background/accent/text colours and
configurable font sizes are all in, reachable from a settings modal on both screens. See `PROGRESS.md`.

- **Current state:** every colour beyond the Fluent defaults is a named brush in `App.axaml`, defined
  once per variant under `ResourceDictionary.ThemeDictionaries` and consumed through `DynamicResource`.
  On top of that, `Theming` feeds Fluent its own `ColorPaletteResources` palette, expanded from three
  configurable colours per variant (background, accent, text) over a choice of four palettes, so custom
  colours reach the inside of Fluent's controls rather than only the window. Font sizes are two
  configurable bases — UI and monospace — exposed as named resources the XAML reads by role.
- **Not done:** repainting a colour change without a restart. Fluent only reads its palette while
  loading, and reloading it corrupts every ComboBox (AvaloniaUI/Avalonia#17917, open), so colours apply
  at startup and the settings screen offers a restart plus a live preview tile. The variant and the font
  sizes need no reload and do apply immediately. Also not done: per-workspace themes, and importing or
  sharing a palette as a file.
- **Revisit when:** AvaloniaUI/Avalonia#17917 is fixed, which turns the restart into a live repaint and
  makes the preview tile redundant. Separately, when someone wants to carry a palette between machines —
  the three colours per variant already in `settings.json` are most of that format.
- **Cost:** hours to drop the restart once upstream allows it. An afternoon for import/export.

### Multi-context tree view
Show every `DbContext` in the solution side by side with its migrations, instead of one active context selected from a dropdown.

- **Why parked:** the dropdown covers the common case, and a tree means N times the `dotnet ef` invocations (each of which builds) to populate one screen.
- **Revisit when:** working across several contexts in one session proves to be genuinely painful with the dropdown.
- **Cost:** moderate — mostly the concurrency and caching needed to avoid N sequential builds.

### Migration name templates
Optional timestamp / prefix / ticket-number templating for `migrations add`.

- **Why parked:** the user types the name. A template is a preference, and preferences are cheap to add once the tool is actually in daily use and the desired convention is known.
- **Revisit when:** a naming convention emerges that's tedious to type by hand.
- **Cost:** hours.

### Syntax-highlighted SQL viewer (AvaloniaEdit) — done, folding still parked
Highlighting, line numbers, Ctrl+F and a wrap toggle: **done**. Code folding: still parked.

- **Current state:** the Script tab uses `AvaloniaEdit.TextEditor` (`Avalonia.AvaloniaEdit` 12.0.0) in place of the read-only `TextBox`. Two syntax definitions — `Highlighting/Sql-Light.xshd` and `Highlighting/Sql-Dark.xshd`, derived from AvaloniaEdit's bundled `TSQL-Mode.xshd` — are loaded by `SqlHighlighting` and swapped on `ActualThemeVariantChanged`. There is a file per variant because a `HighlightingColor` holds a literal colour, not a theme resource, so nothing repaints it on a theme switch; this is the same trap that killed the brush-returning converters. The definitions add a number rule and a quoted-identifier rule, so `[dbo].[Blogs]` and `"Blogs"` read as identifiers rather than as string literals across providers. `SearchPanel.Install` gives Ctrl+F, and the Wrap checkbox writes app-wide `DisplaySettings.WrapSql` alongside `WrapOutput`.
- **Not done:** code folding for `BEGIN`/`END` and `GO` batches. AvaloniaEdit has no built-in SQL folding strategy, so that is a hand-written parser — real work, and generated scripts are read top to bottom more often than navigated.
- **Revisit when:** someone is scrolling past collapsed blocks they do not care about. Ctrl+F covers the "find the bit I want" case that folding would otherwise serve.
- **Cost:** a day for a folding strategy that handles nested `BEGIN`/`END` without being fooled by strings and comments.

### Dedicated Settings dialog
A Settings window with app-wide and per-workspace sections, rather than options living inline next to what they affect.

- **Current state:** app-wide preferences exist in the core settings file (`AppSettings.Display`) and per-workspace ones in a file each under `workspaces/`. Both are edited through inline controls — the Wrap checkbox on the output toolbar, the Wrap checkbox on the Script tab, the theme dropdown on the main toolbar, the discovery and skip-build controls in the workspace panel.
- **Why parked:** there are about five options. Moving them into a dialog puts them further from where they are used and buys nothing yet.
- **Revisit when:** the option count outgrows the toolbars, or an option appears that has no natural inline home.
- **Cost:** a few hours; the settings model already separates app-wide from per-workspace.

### View a migration's Up/Down changes — done, per-migration diffing still parked
From the migrations list, select a migration and read what it does. **Done.**

- **Current state:** the Migrations tab is split into the list, a draggable divider and a detail pane. Selecting a migration reads its `.cs` file straight off disk and shows it read-only with C# highlighting — Up and Down are adjacent, so nothing is parsed to separate them. A `SQL` button generates the SQL for that migration alone (`migrations script <previous> <this>`, or `0` as the start for the first one) into a temp file and shows it in the same editor, with the SQL definition swapped in. That costs a build, which is why it is a button rather than something that happens on selection; the result is cached by migration id for the session and dropped whenever the migrations list is reloaded. The file is located by convention — the migrations project is searched for `<id>.cs`, skipping `bin` and `obj` so a stale build-output copy can never be shown as the migration. See `PROGRESS.md`.
- **Not done:** splitting Up from Down into separate panes, and diffing two migrations against each other. The first needs a brace parser that is not fooled by strings and comments; the second is closer to the "migration diffing" item under Not planned. The splitter position is also not remembered between sessions.
- **Revisit when:** someone wants to read Up without Down beside it. Ctrl+F in the pane covers the "find the bit I want" case in the meantime.
- **Cost:** a day for a reliable Up/Down split.

### `dbcontext script` vs `migrations script`
Explore the difference between `dotnet ef dbcontext script` and `dotnet ef migrations script`, and whether EfAssist should expose both.

- **Why parked:** needs investigation before it's even a feature decision.
- **Revisit when:** someone has time to spike it.
- **Cost:** investigation only, for now.

### `dotnet-ef` config file support (.NET 11)
.NET 11 adds config file support to the `dotnet-ef` tools, read from `<repository root>/.config/dotnet-ef.json`. EfAssist may need to read/respect this file too.

- **Why parked:** .NET 11 not yet released/adopted; nothing to support yet.
- **Revisit when:** .NET 11 ships and repos start using the config file.
- **Cost:** unknown until the config file's shape is finalised.

### Pre-release update channel
Let the app opt in to GitHub pre-releases, so a beta can be tried without publishing it as stable.

- **Why parked:** `GithubSource` is constructed with `prerelease: false` and there is one user. A channel switch needs a setting, a UI, and a story for downgrading back to stable.
- **Revisit when:** there is someone to beta-test for.
- **Cost:** hours for the flag, most of the work is the downgrade path.

### UI/UX and layout pass
General review of layout, spacing, information density and visual polish across both screens, beyond one-off fixes made in passing.

- **Why parked:** no single screen is broken; this is a cross-cutting polish pass, not a bug, and needs a concrete list of what to change rather than an open-ended "make it nicer."
- **Revisit when:** there's a specific list of layout/UX complaints to work through, or a design pass is scheduled.
- **Cost:** unknown until scoped — likely several small changes rather than one big one.

### App icon — done
`Assets/app-logo.ico` replaced the Avalonia template default. It is referenced from three places, and each one covers a different surface:

- `MainWindow.axaml` (`Icon=`) — the icon of the running window, which is what the taskbar shows while the app is open. This is the only one that applies when running from Visual Studio, which is why a debug run always looked right.
- `EfAssist.App.csproj` (`<ApplicationIcon>`) — embeds the icon as a Win32 resource in `EfAssist.exe`. Anything that reads an icon off the file rather than off a window — a pinned taskbar shortcut, an Explorer listing, the Start tile, the Alt-Tab entry before the window exists — uses this one. Without it those surfaces fall back to the generic executable icon, which is what made an installed-and-pinned EfAssist look broken while a debug run looked fine ([velopack#581](https://github.com/velopack/velopack/issues/581) is the same confusion from the other direction).
- `build/release.ps1` (`vpk pack --icon`) — the installer and the shortcuts Velopack creates.

All three want the same `.ico`, and it is a multi-resolution one (16 through 256) because Windows picks a size per surface rather than scaling one bitmap.

### Release notes in the update banner
`vpk pack --releaseNotes` takes a markdown file, and Velopack carries the notes through to `VelopackAsset.NotesMarkdown`, so the banner could say what changed rather than only which version.

- **Why parked:** nothing generates release notes yet, so there would be nothing to show.
- **Revisit when:** releases start having notes worth reading.
- **Cost:** hours, once the notes exist.

### Model diagram view — done

Shipped as the Diagrams tab in phases D0–D6. The model is extracted from the EF model snapshot with Roslyn — no build, no database — and drawn as an interactive entity-relationship or class diagram that survives a restart and exports to JSON, SVG, PNG, PDF and Mermaid. See `docs/DIAGRAMS-PLAN.md` for the reasoning and `docs/DIAGRAMS-IMPLEMENTATION.md` for the build.

Five follow-ups came out of it, parked with reasons of their own:

- **MSAGL layout.** The shipped layout is hand-rolled: rank by dependency depth, two barycentre passes, orthogonal routing. Good enough as a starting point, and manual dragging plus Re-layout is the escape hatch. *Revisit when* the automatic arrangement disappoints on a real model of 50+ entities. *Cost:* a package reference and a rewrite of `DiagramLayoutEngine.Compute` and nothing else — the scene, the renderer and every export are downstream of it.
- **Vertical/horizontal layout toggle.** The layout ranks nodes by dependency depth and places each rank as a *column*, stacking that rank's nodes down the page — so a model with only two or three ranks but a dozen entities in each comes out tall and narrow, using far more height than width. A toggle would place ranks as *rows* instead, flowing top-to-bottom with each rank spread across the width, and let the user pick whichever shape suits the model in front of them. *Why parked:* it is more than transposing the coordinates. `DiagramLayoutEngine.Place` is the easy half; edge routing exits a node's right edge and enters the next node's left, and `SceneBuilder.EndMarker` decides which way a crow's foot points from the route's x-direction, so both need an orientation to work from. The barycentre ordering pass is orientation-agnostic and would not change. It also wants a third setting persisted per view alongside the hand-dragged positions, since the two orientations arrange nodes differently enough that one set of positions cannot serve both — the same reason positions are already stored per `DiagramKind`. *Revisit when:* someone hits a model where the column layout wastes the screen, which is most likely on a wide, shallow model. *Cost:* around a day, most of it in routing and markers; MSAGL above would subsume it, since a real layout engine takes a flow direction as a parameter.
- **Per-migration diagram diffing.** Draw the model as of migration N, or highlight what migration N changed. Every migration has its own snapshot in git history, so the data is there. *Why parked:* needs a git read or a checkout to get the older snapshot, which is a new dependency and a new failure mode. `ModelSnapshotLocator.FindForMigration` is already written for it. *Cost:* a day, most of it in getting the historical file safely.
- **Cross-context diagrams.** One diagram spanning two `DbContext`s. *Why parked:* two contexts are usually two databases, and drawing them as one schema implies a join that cannot exist. *Revisit when* someone has a genuine multi-context single-database model.
- **Diagram editing.** Editing an entity on the diagram and generating a migration from it. *Why parked:* that is a modelling tool, not a migrations tool, and it inverts the direction this app works in — code is the source of truth and the snapshot is downstream of it.

---

## Not planned

Listed so they don't get re-proposed as oversights.

- **Editing migration `.cs` files.** That's the IDE's job.
- **`dbcontext scaffold` / reverse engineering.** Different tool, different mental model, much larger surface.
- **Concurrent command execution.** `dotnet ef` commands mutate a database and a filesystem; running two at once is a way to corrupt both. Queue them.
- **Git integration, migration diffing, team-conflict detection.** Interesting, and a whole separate product.
- **Telemetry.** No.
