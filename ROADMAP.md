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
- **Cost:** small. Avalonia and the whole codebase are already cross-platform; this is adding RIDs to a publish command and testing the process-kill behaviour on each OS.

### Installer packaging — MSIX, DMG/pkg, AppImage/deb/rpm
Parked in the original brief, still parked.

- **Why parked:** single-file publish is enough for a developer tool that developers will download and run.
- **Revisit when:** distribution to non-developers, or auto-update becomes a requirement.
- **Cost:** meaningful per platform — signing certificates, notarisation on macOS, packaging metadata.

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

---

## Not planned

Listed so they don't get re-proposed as oversights.

- **Editing migration `.cs` files.** That's the IDE's job.
- **`dbcontext scaffold` / reverse engineering.** Different tool, different mental model, much larger surface.
- **Concurrent command execution.** `dotnet ef` commands mutate a database and a filesystem; running two at once is a way to corrupt both. Queue them.
- **Git integration, migration diffing, team-conflict detection.** Interesting, and a whole separate product.
- **Telemetry.** No.
