# EfAssist — Roadmap

Ideas not being worked on now. Nothing here is rejected — it's parked with a reason, so the decision doesn't have to be re-argued from scratch. Two sections: things cut from the v1 plan, and things noticed in the shipped app since. Items that have since been built keep their entry, marked done, because the reasoning is worth as much afterwards as before.

See `PLAN.md` for the v1 scope and the decisions that put the first group here, and `PROGRESS.md` for what is actually built.

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

- **Current state:** `build/release.ps1` runs a self-contained publish and hands the directory to Velopack (`vpk pack --msi`), which produces `EfAssist-win-Setup.exe`, `EfAssist-win.msi`, a portable zip, and a delta package against the previous release. `vpk upload github` publishes them to GitHub Releases, which is the feed the app reads. The `.msi` is a machine-wide bootstrap for Group Policy / Intune deployment: it installs the same per-user application for every user, and updates still come from the in-app updater rather than from a new `.msi`. In the app, `IAppUpdater`/`VelopackUpdater` back an `UpdateViewModel`: a silent check shortly after launch, a manual "Check for updates" button in the settings modal, a dismissible app-wide banner offering "Update and restart", and an "Update now" button on the home page once that banner has been dismissed. See `PROGRESS.md`.
- **Not done:** code signing. Nothing produced is signed, so SmartScreen warns on first run until download reputation builds. `vpk pack --signParams` (or `--azureTrustedSignFile`) is the hook; the work is buying and handling a certificate, not the wiring. MSIX, DMG/pkg and AppImage/deb/rpm remain parked — MSIX because the `.msi` already covers managed deployment, the rest with the platforms themselves.
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

### Dedicated Settings dialog — done, per-workspace sections still parked
A Settings window, rather than every option living inline next to what it affects. **Done** as a modal.

- **Current state:** `SettingsWindow` is modal, reachable from a gear button in the workspace toolbar, from the home page's action row, and from `Ctrl+,`. Three tabs — Appearance (variant, palette preset, the three colours per variant, the two font sizes, the preview tile), Tools (`Update dotnet-ef`) and Updates (version and `Check for updates`). Its `DataContext` is the shell's `MainWindowViewModel`, so those tabs drive the same commands the rest of the app does rather than a second copy. There is no OK button: every change saves as it is made and `Close` is the only action. See `PROGRESS.md`.
- **Not done:** per-workspace sections. Options that belong to one workspace — discovery mode, migration refresh, skip-build, idempotent, the script output folder — are still inline in the workspace panel, and app-wide display toggles that sit where they are used (the two Wrap checkboxes, line numbers, sort order) stayed there too. The dialog took the options that had no natural inline home; it did not become the only place options live.
- **Revisit when:** an inline control is genuinely hard to find, or a per-workspace option appears that has nowhere to sit. Moving a checkbox from beside the thing it affects into a dialog is a downgrade unless the toolbar is out of room.
- **Cost:** hours per section; the settings model already separates app-wide from per-workspace.

### View a migration's Up/Down changes — done, SQL diffing still parked
From the migrations list, select a migration and read what it does. **Done.**

- **Current state:** the Migrations tab is split into the list, a draggable divider and a detail pane. Selecting a migration reads its `.cs` file straight off disk and shows it read-only with C# highlighting — Up and Down are adjacent, so nothing is parsed to separate them. A `SQL` button generates the SQL for that migration alone (`migrations script <previous> <this>`, or `0` as the start for the first one) into a temp file and shows it in the same editor, with the SQL definition swapped in. That costs a build, which is why it is a button rather than something that happens on selection; the result is cached by migration id for the session and dropped whenever the migrations list is reloaded. The file is located by convention — the migrations project is searched for `<id>.cs`, skipping `bin` and `obj` so a stale build-output copy can never be shown as the migration. See `PROGRESS.md`.
- **Not done:** splitting Up from Down into separate panes, and diffing two migrations' SQL against each other. The *model* as of a migration, and what it changed, is now on the Diagrams tab instead. The first needs a brace parser that is not fooled by strings and comments; the second is closer to the "migration diffing" item under Not planned. The splitter position is also not remembered between sessions.
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

### UI/UX and layout pass — partly done, the full pass still parked
General review of layout, spacing, information density and visual polish across both screens. A run of
density and navigation changes has landed; the open-ended polish pass has not.

- **Current state (done):** the left workspace panel collapses to a 28px rail with a reopen button, and
  the state persists (`DisplaySettings.LeftPanelExpanded`, `Ctrl+B`). The output panel and the migration
  actions group collapse the same way, each with its own persisted flag. Tab navigation has `Alt+1`–`Alt+4`
  accelerators, advertised as a tooltip on each tab header, driven by `SelectTabCommand` — which ignores an
  unparseable parameter rather than throwing at a keystroke. "Open folder" buttons reveal the open
  workspace's folder and any recent entry's folder in the OS file browser without opening the workspace
  (`ShowWorkspaceFolderCommand` / `ShowRecentFolderCommand`). The main toolbar was re-laid-out around the
  workspace name (`WorkspaceName`, which keeps a folder's whole name and drops a solution file's
  extension), and the button set moved to icons. Earlier passes added row numbers on the migrations list
  and a visible grab handle on the output splitter.
- **Not done:** the cross-cutting pass itself — a deliberate review of spacing, alignment and information
  density across both screens, rather than the individual complaints fixed as they were noticed. Window
  size and position persist, but the Migrations tab splitter position does not (see `PROGRESS.md`
  § Deliberate shortcuts). There is no keyboard-shortcut reference anywhere in the app; `Ctrl+,`,
  `Ctrl+B` and `Alt+1`–`4` are discoverable only from a tooltip or the source.
- **Revisit when:** there's a specific list of layout/UX complaints to work through, or a design pass is
  scheduled. The one-off route has worked so far, which is an argument for continuing it rather than
  scheduling the big pass.
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

Shipped as the Diagrams tab in phases D0–D6. Per-migration diagrams and diffing followed: the
snapshot picker draws the model as of any migration from its `.Designer.cs`, and marks what that
migration added, removed and changed against the migration before it — no git read needed, because
every migration keeps its own snapshot on disk. The model is extracted from the EF model snapshot with Roslyn — no build, no database — and drawn as an interactive entity-relationship or class diagram that survives a restart and exports to JSON, SVG, PNG, PDF and Mermaid. See `docs/DIAGRAMS-PLAN.md` for the reasoning and `docs/DIAGRAMS-IMPLEMENTATION.md` for the build.

The rank direction toggle followed: a **Top to bottom** / **Left to right** button on the toolbar
runs the ranks as rows rather than as columns, so a wide, shallow model fills a landscape window
instead of coming out tall and narrow. `LayoutOptions.Flow` carries it; each direction keeps its own
hand-dragged positions, the same way the two views already do.

Three follow-ups came out of it, parked with reasons of their own:

- **MSAGL layout.** The shipped layout is hand-rolled: rank by dependency depth, two barycentre passes, orthogonal routing. Good enough as a starting point, and manual dragging plus Re-layout is the escape hatch. *Revisit when* the automatic arrangement disappoints on a real model of 50+ entities. *Cost:* a package reference and a rewrite of `DiagramLayoutEngine.Compute` and nothing else — the scene, the renderer and every export are downstream of it.
- **Cross-context diagrams.** One diagram spanning two `DbContext`s. *Why parked:* two contexts are usually two databases, and drawing them as one schema implies a join that cannot exist. *Revisit when* someone has a genuine multi-context single-database model.
- **Diagram editing.** Editing an entity on the diagram and generating a migration from it. *Why parked:* that is a modelling tool, not a migrations tool, and it inverts the direction this app works in — code is the source of truth and the snapshot is downstream of it.

---

## Found since v1, parked with a reason

Gaps noticed in the shipped app rather than cut from the v1 plan. Same rules: nothing here is
rejected, and each carries the reason it is not being done now.

### `dotnet ef migrations bundle`
Produce a self-contained migration bundle — an executable that applies migrations on a machine with
no SDK and no source.

- **Why parked:** nothing more than that it has not been asked for yet. It is the one significant EF
  verb the app does not cover: `EfArgs` has list, add, remove, script, `database update`,
  `database drop`, `migrations has-pending-model-changes`, `dbcontext list` and `dbcontext info`.
- **Revisit when:** migrations need to reach a server that has no SDK on it. That is the case a
  bundle exists for, and the generated script — which the app does produce — is the alternative most
  people reach for first.
- **Cost:** small. One `Build` call in `EfArgs`, a save dialog, and the `--self-contained` /
  `--target-runtime` options if a bundle for another OS is ever wanted. The confirmation and output
  plumbing already exist.

### `dotnet ef dbcontext optimize`
Generate a compiled model, which cuts EF's startup cost on a large model.

- **Why parked:** it writes generated source into the user's project, which is a different kind of
  action from everything else the app does — every other command either reads, or changes a database,
  or writes a migration the user asked for by name. Generated model files also go stale silently when
  the model changes, and the app would be the thing that put them there.
- **Revisit when:** someone has a model big enough for startup time to matter, and wants the app to
  own regenerating it after every migration rather than doing it by hand.
- **Cost:** hours for the verb itself. The staleness story is the part that needs thought.

### Filter box on the migrations list
Type to narrow the list to matching migration names.

- **Why parked:** the list is short on most projects, and it is sorted, so scrolling finds things. The
  detail pane already has Ctrl+F for searching *inside* a migration, which is the harder need.
- **Revisit when:** a project has enough migrations that finding one by eye is tedious. Several
  hundred is where this stops being a preference.
- **Cost:** small — the Diagrams tab already does exactly this in `DiagramsViewModel.RefreshMatches`,
  and `Migrations` is a view over `_ordered` already, so filtering it changes no other behaviour.
  Whatever ships should inherit that method's known limit: a substring scan on every keystroke.

### Keyboard shortcut reference
Somewhere in the app that lists the shortcuts.

- **Current state:** `Ctrl+,` opens settings, `Ctrl+B` collapses the left panel, `Alt+1`–`Alt+4`
  select a tab, and Ctrl+F searches inside an editor. Only the `Alt` ones advertise themselves, as a
  tooltip on each tab header; the rest are discoverable from the source and nowhere else.
- **Why parked:** there are four to learn, and the app is usable without knowing any of them.
- **Revisit when:** the list grows past what a tooltip can carry, or someone asks what the shortcuts
  are — which is the actual signal that they are undiscoverable.
- **Cost:** an hour for a section in the settings modal. A `?` overlay is a day and buys little more.

### A one-click copy of the command line
A button that copies just the `dotnet ef …` that ran.

- **Current state:** the command is not hidden. Every run echoes itself into the output panel as
  `> dotnet ef …` before anything else, and `Copy diagnostics` puts a `Command:` line at the top of
  the block it copies, alongside the working directory, the exit code and every output line.
- **Why parked:** the need is already met twice over; this is only about saving a trim of the
  clipboard. It went on the list as "make the app teach the CLI", and it turns out the app already
  does.
- **Revisit when:** someone is regularly pasting a command into a terminal and editing out the rest of
  the diagnostics block first.
- **Cost:** under an hour — `EfResult.CommandLine` already exists and is what both surfaces read.

### Persist the Migrations tab splitter
See `PROGRESS.md` § Deliberate shortcuts: the column widths are proportional and not remembered, while
the window size, the panel collapse states and the output height all are. Two fields on
`DisplaySettings` and the same wiring those use. Revisit when someone resizes it every session.

### Code signing
Already recorded under **Installer packaging** above, as the outstanding half of that item. Repeated
here only so it is not read as an oversight: nothing produced is signed, SmartScreen warns on first
run, and the work is buying and handling a certificate rather than wiring `vpk pack --signParams`.

### Exercise the update path against real releases
Not a feature — a verification that has never been done.

- **Current state:** `PROGRESS.md` § Phase 6 records that the download-and-restart path has never run
  for real. The check, the banner and the failure handling are covered by unit tests against a fake;
  Velopack's own download and apply are not. At the time there was no published release to update
  from or to.
- **Why it matters now:** there are releases, so the thing that could not be tested can be. An
  auto-updater that has never updated anything is a feature on trust.
- **Revisit when:** now, ahead of most of this list — installing an older version, publishing a newer
  one and watching a real install take it is an afternoon, and it either confirms the feature or finds
  the bug before a user does.
- **Cost:** an afternoon, and it costs a throwaway release to update from.

---

## Not planned

Listed so they don't get re-proposed as oversights.

- **Editing migration `.cs` files.** That's the IDE's job.
- **`dbcontext scaffold` / reverse engineering.** Different tool, different mental model, much larger surface.
- **Concurrent command execution.** `dotnet ef` commands mutate a database and a filesystem; running two at once is a way to corrupt both. Queue them.
- **Git integration, migration diffing, team-conflict detection.** Interesting, and a whole separate product.
- **Telemetry.** No.
