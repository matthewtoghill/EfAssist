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

### Dedicated Settings dialog — done, rebuilt as a searchable two-pane screen
A Settings window, rather than every option living inline next to what it affects. **Done** as a modal,
and since redesigned: the three tabs became a category list, and the options that had nowhere to live
came in with it.

- **Current state:** `SettingsWindow` is modal, reachable from a gear button in the workspace toolbar,
  from the home page's action row, from `Ctrl+,`, and — since the shortcut sheet folded into it — from
  `F1` and `Ctrl+/`. It opens at 1000×760 centred on the shell, is resizable down to 760×560, and
  remembers its own size — not its position — in `DisplaySettings.SettingsWindow`: it is a modal over
  the shell, so a remembered position would open it on whichever monitor it was last dragged to. A 224px category list replaces the tabs: Theme, Text and
  layout, Code and console, Workspace defaults, Diagrams, Tools, Shortcuts, Updates and about. A search
  box above the list filters the rows inside every pane and hides the categories with nothing left,
  showing "3 of 6 shown" beside the pane heading and a match count against each category. The filtering
  lives in the view (`Views/SettingsSearch.cs`, an attached property carrying each row's extra search
  terms) because the rows are declared in XAML: a parallel list in a view model would be a second copy
  to keep in step, and the first thing to rot would be the setting nobody could find. Theme now shows all
  nine palettes as a gallery of tiles, each painted in its own colours rather than as three chips, plus a
  WCAG contrast grade on the chosen trio — the pickers will happily accept a pair nobody can read. Its
  `DataContext` is still the shell's `MainWindowViewModel`, so every row drives the same commands and
  properties the rest of the app does rather than a second copy. There is no OK button: every change
  saves as it is made and `Close` is the only action. See `PROGRESS.md`.
- **Also in:** workspace defaults (`AppSettings.WorkspaceDefaults`) seed a workspace's own file the first
  time it is opened and are never applied again, so changing one cannot reach back into a solution
  already set up; `WrapOutput` and `SortNewestFirst` are finally reachable from Settings as well as from
  the controls beside what they affect; and About carries export, import and reset for the app-wide half
  of the settings.
- **Not done:** per-workspace panes. The workspace's own options stay inline on the workspace screen —
  Settings offers the defaults a new one starts from, not a second place to change the open one. Also not
  done: keyboard-only navigation of the palette gallery beyond arrow keys, and rebinding a shortcut.
- **Revisit when:** an inline control proves genuinely hard to find, or someone wants the open
  workspace's own options in Settings too — at which point the honest shape is a scope switch at the top
  of that pane, not a duplicate set of rows.
- **Cost:** as estimated per section. The search was the surprise: cheap in the view, and it would have
  been a week as a data-driven row engine.

### View a migration's Up/Down changes — done, SQL diffing still parked
From the migrations list, select a migration and read what it does. **Done.**

- **Current state:** the Migrations tab is split into the list, a draggable divider and a detail pane. Selecting a migration reads its `.cs` file straight off disk and shows it read-only with C# highlighting — Up and Down are adjacent, so nothing is parsed to separate them. A `SQL` button generates the SQL for that migration alone (`migrations script <previous> <this>`, or `0` as the start for the first one) into a temp file and shows it in the same editor, with the SQL definition swapped in. An `Up` / `Down` switch beside it generates the reverse range instead, for the SQL that rolls the migration back. That costs a build, which is why it is a button rather than something that happens on selection; the result is cached by migration id, direction and the idempotent flag for the session, and dropped whenever the migrations list is reloaded. The file is located by convention — the migrations project is searched for `<id>.cs`, skipping `bin` and `obj` so a stale build-output copy can never be shown as the migration. See `PROGRESS.md`.
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

### UI/UX and layout pass — done
Built as the Option A layout, on `feat/app-layout-redesign`. See `docs/dev/LAYOUT-REDESIGN-PLAN.md` for
the plan and `docs/dev/PROGRESS.md` § Layout pass for what landed and what was decided along the way.

- **What changed:** the 320px options panel is gone — its pickers are a breadcrumb in the top bar and
  its switches are a Run options popover with a count badge; the tab strip is a 62px icon rail; the
  200px console folded to a one-line strip that opens onto Activity (one card per command, with the
  diagnosis attached to the run that caused it) or the raw console; each screen gained a header with
  one primary action; the Migrations actions expander was replaced by per-migration actions in the
  detail pane, a filter, and a "Database is here" marker; whole-database actions moved to Tools,
  which is now four cards; the Diagrams toolbar was regrouped and zoom moved onto the surface; and
  `F1` opens a shortcut sheet, which the app had no equivalent of.
- **Still open:** the Migrations splitter position is still not persisted (see `PROGRESS.md`
  § Deliberate shortcuts), and no one has looked at the new screens in the dark variant or at a
  non-default font size yet.

### Activity across restarts
Keep the per-command Activity list — command, outcome, duration, diagnosis — after the app closes,
instead of only for the session.

- **Why parked:** EF output carries server names and connection strings, so persisting it means
  deciding what to redact, where to put a size cap, and what to do with a history whose console
  lines no longer exist. In memory it is free and cannot leak.
- **Revisit when:** someone wants yesterday's failure back, or the same failure has to be compared
  across two runs of the app.
- **Cost:** a schema addition beside the workspace settings, a redaction pass, a cap, and a load
  path — the recording itself already exists.

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
every migration keeps its own snapshot on disk. The model is extracted from the EF model snapshot with Roslyn — no build, no database — and drawn as an interactive entity-relationship or class diagram that survives a restart and exports to JSON, SVG, PNG, PDF and Mermaid. See `docs/dev/DIAGRAMS-PLAN.md` for the reasoning and `docs/dev/DIAGRAMS-IMPLEMENTATION.md` for the build.

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

### Filter box on the migrations list — done
Type to narrow the list to matching migration names: **done**.

- **Current state:** a filter box above the list, with a clear button inside it that appears only
  while `Migrations.IsFiltered`. `MigrationsViewModel.Filter` rebuilds the displayed list on every
  keystroke and matches on both name and id, so a remembered name and a pasted timestamp each find
  their row. Display only: it never changes what a command runs, and `Summary` reads "n of m
  migrations · filtered" while it is set, so a narrowed list cannot be mistaken for the whole one.
  It kept the known limit it was costed with — a substring scan per keystroke.
- **What changed the mind:** it came along with the layout pass, sharing its row with the list's own
  refresh and sort buttons, so it cost less than pricing it alone had suggested.
- **Cost:** as estimated — small.

### Keyboard shortcut reference — done
Somewhere in the app that lists the shortcuts: **done**, twice over — a reference sheet, and badges on
the buttons themselves.

- **Current state:** every gesture is on `Ctrl`. `Ctrl+1`–`Ctrl+4` select a screen, `Ctrl+,` opens
  settings, ``Ctrl+` `` folds the output panel, `Ctrl+/` (or `F1`) opens the sheet, and `Ctrl+F`/`F3`
  search inside an editor. They were split across `Alt` for navigation and `Ctrl` for commands, which
  is the Windows convention, but `Ctrl+,` and ``Ctrl+` `` are worth more as conventions than the split
  was, and `Ctrl+1..n` for "go to view n" is what a browser does — so one held key now reveals the
  lot. The sheet renders `EfAssist.App.ViewModels.Shortcuts`, which is the
  single list a new binding has to be added to. On top of that, holding `Ctrl` for 400ms badges every
  button that gesture reaches (`Views/ShortcutHint.cs`, an attached property plus an `AdornerLayer`
  badge), the way Windows labels access keys. The sheet is no longer a window of its own: `F1` and `Ctrl+/` open
  the settings screen on its Shortcuts category, which renders the same list. Two windows meant two
  places for the app's chrome to drift.
- **What changed the mind:** someone asked what the shortcuts were, which was the recorded trigger.
  The tooltips answered it only once the mouse was already on the button, which is the wrong end of
  the problem for a keyboard shortcut.
- **Cost:** as estimated for the sheet. The badges were an afternoon, most of it spent on two
  Avalonia traps worth knowing: an adorner is measured and clipped to the bounds it adorns, so a
  badge needs a `StackPanel` wrapper and `IsClipEnabled` set on the adorner rather than the adorned
  control; and a key that something else has handled never reaches a plain routed handler, so an
  observer of shortcuts has to register with `handledEventsToo`.

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
