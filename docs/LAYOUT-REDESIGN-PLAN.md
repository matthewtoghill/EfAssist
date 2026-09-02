# Layout redesign — implementation plan

The agreed design is "Option A" from the design canvas: a top bar carrying the three
project/context choices, a 60px icon rail in place of the tab strip, one primary action per
screen, command flags behind a **Run options** popover, and the output console folded to a single
line that expands into **Activity | Raw output**.

Palette, type and control metrics do not change: everything already comes from `App.axaml`'s
theme dictionaries and `Theming.ApplyFontSizes`. This is a layout and information-architecture
pass, not a re-skin.

## Decisions taken before starting

| Question | Decision |
| --- | --- |
| How long Activity remembers | Session only, in memory. A capped list on `CommandSession`; no settings schema change, nothing written to disk. |
| Narrow windows (`MinWidth` is 900) | The breadcrumb pills degrade in place: captions drop, values truncate, the environment summary hides, tooltips carry the full text. No second layout mode. |
| New behaviour in scope | Migration list filter box; `Alt+/` shortcut sheet; Activity "Run again" and "Show in raw output"; a `ScriptRange.FromSelected` preset. |
| Delivery | One commit per phase on `feat/app-layout-redesign`, each building and passing `dotnet test`. |

## What moves where

| Today | After |
| --- | --- |
| Left panel (320px): startup project, migrations project, DbContext | Top bar breadcrumb pills |
| Left panel: context discovery, migration list refresh, `--no-build`, `--no-connect`, `--idempotent` | Run options popover, with an "N on" count on the button |
| `TabControl` header strip | Icon rail, `Alt+1`–`Alt+4` unchanged |
| Migrations "Actions" expander: add, update to latest/selected, remove | Contextual actions on the selected migration, plus an Add migration flyout in the screen header |
| Migrations "Actions" expander: revert all, drop database | Tools screen, in a marked-off whole-database card |
| Output pane (200px, always sized) | One-line strip that expands to Activity / Raw output |
| Diagrams toolbar: ten equal buttons | Grouped segments, plus a floating zoom cluster and the diff legend on the surface |
| Tools: one button | Cards: pending model changes, environment, workspace, whole-database actions |

## Phases

Each phase ends with `dotnet build EfAssist.slnx` and `dotnet test EfAssist.slnx` green, and a
commit. Phases are ordered so the app is usable at every boundary.

### Phase 1 — Shell: top bar, icon rail, status bar

- `App.axaml`: add the four rail glyphs (migrations, script, diagrams, tools) and an output glyph
  as `StreamGeometry` resources beside the existing folder and settings icons.
- `MainWindow.axaml`: replace the toolbar `Grid` with the breadcrumb bar (pills bound to the
  existing `Projects` / `Contexts` / `StartupProject` / `MigrationsProject` / `SelectedContext`
  properties); replace the `TabControl` chrome with a rail bound to `SelectTabCommand` and
  `SelectedTabIndex`, keeping each tab's content as-is; delete the left panel and its rail/toggle.
- Narrow degradation: a window-width trigger that hides the pill captions and the environment
  summary, with `ToolTip.Tip` on each pill carrying the full value.
- The panel keeps the five command options for now, and keeps `Ctrl+B`. It is deleted in phase 2,
  once the popover has somewhere to put them: no phase boundary should leave an option
  unreachable.
- No view-model changes — every command and binding already exists.

### Phase 2 — Run options popover

- `MainWindow.axaml`: a `Button` with a `Flyout` holding the five controls the left panel used to
  carry, bound to the same properties (`NoBuild`, `Migrations.Offline`, `Idempotent`,
  `DiscoveryMode`, `Migrations.RefreshMode`).
- `MainWindowViewModel`: an `ActiveRunOptionCount` computed property for the button's badge, kept
  in sync from the existing `partial void On...Changed` hooks.
- Delete the options panel, `LeftPanelExpanded`, `ToggleLeftPanelCommand` and the `Ctrl+B`
  binding. `DisplaySettings.LeftPanelExpanded` stays in the settings file as a dead property so
  older settings files still load.
- Tests: `MainWindowViewModelTests` covers the count for each combination.

### Phase 3 — Output strip and Activity

- `CommandSession`: record one `CommandRun` per `RunAsync` / `RunLocalAsync` — label, argument
  list, exit state, duration, the diagnosis, and the index of its first line in `Output` (which is
  what "Show in raw output" scrolls to). Capped at 50 entries, oldest dropped. Nothing persisted.
  A `Destructive` flag on the run, set by the caller, gates "Run again": re-running a database
  write from a history card would bypass the confirmation the original went through, so those
  cards show the command but not the button.
- `MainWindow.axaml` / `.axaml.cs`: the strip (last command, result, duration, count) and the
  expanded pane with an `Activity | Raw output` switch; Activity is an `ItemsControl` of run
  cards; "Show in raw output" flips the switch and scrolls `OutputScroller` to the recorded line.
- Rail: an unread-failure dot, cleared when the pane is opened on Activity.
- Status bar: last failure named, next to `Session.StatusMessage`.
- Tests: `CommandSessionTests` for the cap, the recorded fields, the line index, and that a
  cancelled run is recorded as cancelled rather than failed.

### Phase 4 — Migrations screen

- Contextual actions on the selected migration: "Apply up to here" (`UpdateToSelectedCommand`),
  "Script from here" (sets the new `ScriptRange.FromSelected` and switches tab), "Remove"
  (`RemoveCommand`), plus the existing Source / SQL / Copy / Open file row.
- An Add migration flyout in the screen header: the name box, the validation message and the
  output-folder box that used to live in the expander, bound to the same properties.
- The database-head marker in the list: a row rendered between the last pending and the first
  applied migration. `MigrationsViewModel` exposes where the boundary is; the marker is hidden
  when applied state is Unknown (offline), where the app cannot honestly claim to know.
- Filter box over the list: `MigrationsViewModel.Filter` plus a filtered view; the summary line
  reports "n of m" while filtering.
- Delete the Actions expander, `MigrationActionsExpanded` and its `Ctrl+`-nothing binding;
  `DisplaySettings.MigrationActionsExpanded` stays as a dead property (its settings test is
  updated to assert the round-trip of what remains).

### Phase 5 — Script screen

- One band for range and destination, in the mockup's order.
- `ScriptRange.FromSelected`: scripts from the migration selected on the Migrations screen to the
  latest. `ScriptViewModel` gains the range case, the file-name suggestion and the range warning
  for it; `ScriptViewModelTests` and `ScriptFileNameTests` extended.
- Viewer header states what the file is: generated time, migrations, statements, idempotent, size.
  Statement count comes from the SQL already read back for the viewer.

### Phase 6 — Diagrams screen

- Toolbar regrouped: Tables/Classes and flow direction as two-state segmented pairs (existing
  `SwitchViewCommand` / `SwitchFlowCommand`), then lock, re-layout, view options.
- Snapshot picker and "Mark changes" move next to the primary "Generate diagram".
- Zoom cluster and the diff legend move onto the surface as overlays. The four zoom buttons keep
  their `x:Name`s so the existing code-behind wiring in `MainWindow.axaml.cs` is untouched.

### Phase 7 — Tools screen

- Four cards: pending model changes (existing `Tools.*`), environment (versions, `Update
  dotnet-ef`, `Copy install command`, app update offer from `Update.*`), workspace (paths, refresh
  contexts, open folder, open settings file, close workspace), and whole-database actions
  (`Migrations.RevertAllCommand`, `Migrations.DropDatabaseCommand`).
- The whole-database card binds straight to `MigrationsViewModel`; the commands do not move, only
  the buttons.

### Phase 8 — `Alt+/` shortcut sheet

- A small `ShortcutsWindow` listing the shortcuts, opened by a key binding and by the top bar
  button. One source of truth for the list, so a binding added later is added once.

### Phase 9 — Dark pass and docs

- Walk every new surface in both variants, checking the new overlays and the popover against the
  Dark dictionary (surfaces read from `PanelBackgroundBrush` / `SubtleBorderBrush`, never a
  literal).
- Update `docs/PROGRESS.md` (what landed, what was deliberately left) and the
  "UI/UX and layout pass" entry in `docs/ROADMAP.md`, which this plan closes out.

## Deliberately not in this pass

- Persisting Activity across restarts — parked with a trigger in `ROADMAP.md`.
- "Run again" for destructive commands (see phase 3).
- Splitter positions on the Migrations screen, still unremembered (`PROGRESS.md` already records
  this).
- Option B's timeline spine and Option C's command palette. Both remain available as later
  additions; neither depends on the other.
