# EfMigrateHub — Roadmap

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

- **Current state:** `build/release.ps1` runs a self-contained publish and hands the directory to Velopack (`vpk pack`), which produces `EfMigrateHub-win-Setup.exe`, a portable zip, and a delta package against the previous release. `vpk upload github` publishes them to GitHub Releases, which is the feed the app reads. In the app, `IAppUpdater`/`VelopackUpdater` back an `UpdateViewModel`: a silent check shortly after launch, a manual "Check for updates" button on the home page, and a dismissible banner offering "Update and restart". See `PROGRESS.md`.
- **Not done:** code signing. Nothing produced is signed, so SmartScreen warns on first run until download reputation builds. `vpk pack --signParams` (or `--azureTrustedSignFile`) is the hook; the work is buying and handling a certificate, not the wiring. MSIX, DMG/pkg and AppImage/deb/rpm remain parked with the platforms themselves.
- **Revisit when:** the SmartScreen warning becomes a real obstacle to someone installing it, or macOS/Linux ship.
- **Cost:** signing is a certificate purchase plus an afternoon. Notarisation on macOS is its own day.

### Theme support — light/dark/system done, custom schemes still parked
Light and dark themes selectable from the UI: **done**. Broader theming (accent colour, font size, custom colour schemes): still parked.

- **Current state:** every colour beyond the Fluent defaults is a named brush in `App.axaml`, defined twice under `ResourceDictionary.ThemeDictionaries` — one value tuned for light, one for dark — and consumed through `DynamicResource`, so a switch repaints live. Semantic style classes (`caution`, `danger`, `diagnosis`, the state badges, the console channels) mean the colour lives in the style rather than at each usage site. A System/Light/Dark dropdown in the toolbar writes `DisplaySettings.Theme` and calls `App.ApplyTheme`; System stays on `ThemeVariant.Default`, so Avalonia keeps following the OS including changes made while running.
- **Not done:** accent colour, font size, and user-defined schemes. Those were the "more for custom schemes" half of the original estimate and remain parked — nobody has asked, and each one is a new settings surface to maintain.
- **Revisit when:** someone wants an accent colour or a larger UI font. The groundwork is in place: both would be another `DisplaySettings` field plus a style, not another colour audit.
- **Cost:** hours each, now that the colours are resources.

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

### Update dotnet-ef tool from the app
From the home page, offer a way to update the `dotnet-ef` global/local tool.

- **Why parked:** not scoped for v1; needs thought on global vs local tool installs and permission/elevation concerns.
- **Revisit when:** prioritised for a later version.
- **Cost:** small-moderate.

### `dbcontext script` vs `migrations script`
Explore the difference between `dotnet ef dbcontext script` and `dotnet ef migrations script`, and whether EfMigrateHub should expose both.

- **Why parked:** needs investigation before it's even a feature decision.
- **Revisit when:** someone has time to spike it.
- **Cost:** investigation only, for now.

### `dotnet-ef` config file support (.NET 11)
.NET 11 adds config file support to the `dotnet-ef` tools, read from `<repository root>/.config/dotnet-ef.json`. EfMigrateHub may need to read/respect this file too.

- **Why parked:** .NET 11 not yet released/adopted; nothing to support yet.
- **Revisit when:** .NET 11 ships and repos start using the config file.
- **Cost:** unknown until the config file's shape is finalised.

### Pre-release update channel
Let the app opt in to GitHub pre-releases, so a beta can be tried without publishing it as stable.

- **Why parked:** `GithubSource` is constructed with `prerelease: false` and there is one user. A channel switch needs a setting, a UI, and a story for downgrading back to stable.
- **Revisit when:** there is someone to beta-test for.
- **Cost:** hours for the flag, most of the work is the downgrade path.

### Release notes in the update banner
`vpk pack --releaseNotes` takes a markdown file, and Velopack carries the notes through to `VelopackAsset.NotesMarkdown`, so the banner could say what changed rather than only which version.

- **Why parked:** nothing generates release notes yet, so there would be nothing to show.
- **Revisit when:** releases start having notes worth reading.
- **Cost:** hours, once the notes exist.

---

## Not planned

Listed so they don't get re-proposed as oversights.

- **Editing migration `.cs` files.** That's the IDE's job.
- **`dbcontext scaffold` / reverse engineering.** Different tool, different mental model, much larger surface.
- **Concurrent command execution.** `dotnet ef` commands mutate a database and a filesystem; running two at once is a way to corrupt both. Queue them.
- **Git integration, migration diffing, team-conflict detection.** Interesting, and a whole separate product.
- **Telemetry.** No.
