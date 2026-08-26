# EfAssist — Model diagrams: implementation plan

The actionable build plan for the Diagrams tab. `DIAGRAMS-PLAN.md` is the research and the reasoning;
this document is the work. Every decision it depends on is settled in `DIAGRAMS-PLAN.md` §8.

Status: **D0 to D6 done.** See `PROGRESS.md` for what was built, what the rendering turned up, and where
the plan below was deliberately departed from (export palette, export destination, PNG scale, the
export menu).

---

## 0. The decisions this is built on

| Decision | Consequence for this plan |
| --- | --- |
| Source is the EF model snapshot (`<Context>ModelSnapshot.cs`) | No build, no database, no connection string. Generation is a file read plus a parse. |
| Parsed with Roslyn, in `EfAssist.Core` | `Microsoft.CodeAnalysis.CSharp` becomes Core's first package reference. |
| Both ER and class views ship | One extraction, two node-content builders, one layout, one renderer. |
| Diagram may lag the code | A staleness badge is a required part of the feature, not polish. |
| No migrations → empty state | No source-scanning fallback. |
| `CommandSession.RunLocalAsync` | In-process work reuses the existing busy state, Cancel button and console. |
| Exports: JSON, PNG, SVG, PDF, Mermaid | SVG and Mermaid hand-written; PNG and PDF via SkiaSharp. |
| PDF is one page sized to the diagram | No tiling. |
| Under 100 entities expected | Hand-rolled layered layout, no surface virtualisation. |

---

## 1. Architecture at a glance

```
snapshot file (.cs on disk)
        │  ModelSnapshotLocator   — find the file for the selected context
        ▼
   Roslyn syntax tree
        │  ModelSnapshotParser    — walk invocations
        ▼
   DiagramModel                   ─────────────────────────────► JsonExport, MermaidWriter
        │  DiagramNodeContent     — apply DiagramKind + view options
        ▼
   node rows + edge list
        │  DiagramLayout.Compute  — layered layout, pure function
        ▼
   DiagramScene                   — rects, lines, text, markers; roles not colours
        ├──► DiagramSurface.Render(DrawingContext)   (on screen, Avalonia)
        ├──► SvgWriter                               (hand-written text)
        └──► SkiaExport                              (PNG via SKSurface, PDF via SKDocument)
```

Two rules that keep this from turning into a mess:

1. **`DiagramScene` carries roles, not colours.** Every shape has a `DiagramRole`
   (`NodeBackground`, `NodeBorder`, `HeaderBackground`, `HeaderText`, `KeyText`, `Text`, `MutedText`,
   `Edge`, `EdgeLabel`, `Highlight`, `Selection`, `Dimmed`). The renderer resolves role → brush at
   draw time. This is the same trap `ROADMAP.md` records for the SQL `.xshd` files: a baked literal
   colour never repaints on a theme change. Do not bake colours into the scene.
2. **Core never measures text.** `LayoutOptions` carries a `MeasureText` delegate. The app passes one
   backed by Avalonia's `FormattedText`; tests pass a deterministic character-width stub. This is the
   calibration knob — node sizing is the one part of layout that depends on the real font, so it stays
   injectable rather than approximated in Core.

---

## 2. New files

### `src/EfAssist.Core/Diagrams/`

| File | Contents |
| --- | --- |
| `DiagramModel.cs` | `DiagramModel`, `DiagramEntity`, `DiagramProperty`, `DiagramIndex`, `DiagramRelationship`, `Cardinality`. Records, `System.Text.Json`-friendly. Shape is in `DIAGRAMS-PLAN.md` §3.1. |
| `ModelSnapshotLocator.cs` | `Find(string migrationsProjectPath, string contextName) -> string?`. Also `FindForMigration(...)`, which the snapshot picker uses to draw the model as of one migration. |
| `ModelSnapshotParser.cs` | `Parse(string source, string sourcePath) -> DiagramModel`. Roslyn syntax walk. Never throws on unrecognised fluent calls. |
| `DiagramViewOptions.cs` | `DiagramKind` (`EntityRelationship`, `Class`) plus every toggle: property categories, collapse implicit join entities, inline owned types, show indexes, show column types, show nullability, show shadow properties. |
| `DiagramNodeContent.cs` | `Build(DiagramModel, DiagramViewOptions) -> IReadOnlyList<DiagramNode>` — the node title, the visible rows, and the edge list, for the chosen kind. The **only** place the ER/class difference lives. |
| `DiagramLayout.cs` | `Compute(IReadOnlyList<DiagramNode>, IReadOnlyList<DiagramEdge>, LayoutOptions) -> DiagramLayout`. Pure. |
| `DiagramScene.cs` | `DiagramScene`, `DiagramShape` (rect / line / polyline / text / marker), `DiagramRole`. `SceneBuilder.Build(DiagramLayout, DiagramViewOptions, SceneState) -> DiagramScene` where `SceneState` is selection, highlight set and dim set. |
| `SvgWriter.cs` | `Write(DiagramScene, DiagramPalette) -> string`. Hand-written. Entity names become `id` attributes. |
| `MermaidWriter.cs` | `Write(DiagramModel, DiagramViewOptions) -> string`. `erDiagram` or `classDiagram`. |
| `DiagramStore.cs` | `Load` / `Save` / `Path` for the persisted diagram. Owns `SavedDiagram`. |
| `DiagramPalette.cs` | `DiagramRole -> string` (hex) for export. On screen the app resolves roles from theme resources instead; this is only for the file formats, which have no theme. |

### `src/EfAssist.App/`

| File | Contents |
| --- | --- |
| `ViewModels/DiagramsViewModel.cs` | The tab. Generate, Cancel, view toggle, lock, search, options, selection + detail, export commands, persistence. |
| `Views/DiagramSurface.cs` | Custom `Control`. Renders a `DiagramScene`, owns the pan/zoom matrix, hit-tests nodes, drags them when unlocked. No XAML — it draws. |
| `DiagramExport.cs` | PNG (`SKSurface`) and PDF (`SKDocument.CreatePdf`) from a `DiagramScene`. Shares one replay function. |
| `DiagramTheme.cs` | `DiagramRole -> IBrush` from the current theme's `DynamicResource` brushes, plus the export palette for "export with current theme". |

### `tests/EfAssist.Core.Tests/`

`ModelSnapshotLocatorTests.cs`, `ModelSnapshotParserTests.cs`, `DiagramNodeContentTests.cs`,
`DiagramLayoutTests.cs`, `SvgWriterTests.cs`, `MermaidWriterTests.cs`, `DiagramStoreTests.cs`,
`DiagramsViewModelTests.cs`.

New fixtures under `Fixtures/`, following the existing `.txt` convention:

| Fixture | What it covers |
| --- | --- |
| `snapshot-simple.txt` | Two entities, one FK. The happy path. |
| `snapshot-rich.txt` | One-to-many, one-to-one, many-to-many with an implicit join entity, owned type (`OwnsOne` with a nested `b1` builder), TPH with `HasBaseType` + `HasDiscriminator`, composite key, alternate key, named index with annotations, non-default schema. |
| `snapshot-wrapped-args.txt` | A `.HasForeignKey("Ns.Type",\n    "PropName")` call wrapped across lines. This is the case that justifies Roslyn over a line parser and it must have a test. |
| `snapshot-empty.txt` | A context with entities but no relationships. |
| `snapshot-future.txt` | A snapshot containing invented fluent calls, asserting the parser skips them and still returns a usable model. |

---

## 3. Changes to existing files

| File | Change |
| --- | --- |
| `src/EfAssist.Core/EfAssist.Core.csproj` | Add `Microsoft.CodeAnalysis.CSharp`. Pin the version; add a comment saying it is a syntax-only parse, no `MSBuildWorkspace`, no semantic model — so nobody later assumes a compilation is available. |
| `src/EfAssist.App/EfAssist.App.csproj` | Add an explicit `SkiaSharp` `PackageReference` (already in the restore graph at 3.119.4 via `Avalonia.Skia`, so no new native assets). |
| `src/EfAssist.Core/Settings.cs` | `WorkspaceSettings`: `DiagramKind? DiagramKind`, `DiagramViewOptions? DiagramOptions`, `bool DiagramLocked`. `DisplaySettings`: `DiagramKind DefaultDiagramKind` (app-wide default view, per `DIAGRAMS-PLAN.md` §8 Q2). Promote the path-hashing in `SettingsStore.WorkspaceFile` to a public `WorkspaceKey(string path)` so `DiagramStore` uses the same key rather than a third hasher. |
| `src/EfAssist.App/ViewModels/CommandSession.cs` | Add `RunLocalAsync<T>(string label, Func<CancellationToken, Task<T>> work)`. Same `IsRunning` gate, same `_cancellation`, same status line, same `OperationCanceledException` handling as `RunAsync`. Does **not** set `LastResult` or `Diagnosis` — there is no `EfResult`. |
| `src/EfAssist.App/ViewModels/MainWindowViewModel.cs` | Construct `Diagrams`. Add to the `NotifyTargetChanged()` fan-out, to `Restore`, to `Store`/`Persist`, and to both `Clear()` sites. **Replace the `if (value == 1)` magic number in `OnSelectedTabIndexChanged` with a `SelectedTab` enum** (`Migrations = 0, Script = 1, Diagrams = 2, Tools = 3`) and switch on it. |
| `src/EfAssist.App/Views/MainWindow.axaml` | New `TabItem Header="Diagrams"` between Script and Tools. |
| `src/EfAssist.App/Views/MainWindow.axaml.cs` | Wire the surface's file dialogs / open / reveal to the existing `PickSaveFileAsync`, `PickFolderAsync`, `OpenWithShellAsync` helpers, exactly as `Script` does. Re-render the surface on `ActualThemeVariantChanged`. |
| `src/EfAssist.App/Views/SettingsWindow.axaml` | The `DefaultDiagramKind` preference. |
| `docs/PROGRESS.md` | Correct the "**no package references at all**" wording for Core — it described a state, not a rule (`DIAGRAMS-PLAN.md` §8 Q1). Record the phase when done. |
| `docs/ROADMAP.md` | Move MSAGL layout and per-migration diagram diffing into the parked list. |

### Not changed: `samples/SampleEfApp`

Tempting, but no. `MigrationRoundTripTests.cs:79` asserts
`["InitialCreate", "AddBlogUrl"]` verbatim, `ScriptGenerationTests` depends on
"InitialCreate applied, AddBlogUrl pending", and `dbcontext-list.txt` captures exactly two contexts.
Adding a migration or a context to that sample breaks working tests for no gain.

Instead: **new sample project `samples/SampleRichModel`**, outside the solution like `SampleEfApp`,
SQLite, with the rich model the diagram needs to be interesting — one-to-many with navigations on
both ends, a one-to-one, a many-to-many via skip navigations, an owned type, and a two-type TPH
hierarchy. Its snapshot is the source for `snapshot-rich.txt`. Add a `README.md` explaining what it
is for and that it is deliberately outside the solution, matching `SampleEfApp`'s.

---

## 4. Phase D0 — Spike (½ day)

The point is to find out what the parser is actually up against before committing to the shape.

1. Create `samples/SampleRichModel` with the model above. `dotnet ef migrations add InitialCreate`.
2. Copy its snapshot to `tests/.../Fixtures/snapshot-rich.txt`. Write the other four fixtures.
3. Throwaway test: Roslyn-parse `snapshot-rich.txt` and dump every distinct fluent method name found,
   grouped by builder depth.

**Exit criteria:** the list of fluent calls the parser must handle is written down, and every one of
them is present in a fixture. If the rich snapshot contains a construct that does not fit
`DiagramModel` as designed in `DIAGRAMS-PLAN.md` §3.1, fix the shape now rather than in D1.

---

## 5. Phase D1 — Core: extraction (1½ days)

`ModelSnapshotLocator`, `ModelSnapshotParser`, `DiagramModel`. No UI, no layout.

**Locator.** Search the migrations project directory (reusing `MigrationFiles`' explicit walk that
skips `bin`/`obj` and swallows per-directory `IOException`) for a `*ModelSnapshot.cs` whose
`[DbContext(typeof(X))]` attribute names the selected context. Match on the type name, not the file
name — a context can be renamed while the file is not, and the attribute is the authoritative link.
Return null when there is none; that is the empty state, not an error.

**Parser.** `CSharpSyntaxTree.ParseText`, then:

- `modelBuilder.HasAnnotation("ProductVersion", …)` → `EfVersion`.
- Each `modelBuilder.Entity("Ns.Type", b => { … })` → one `DiagramEntity`. The lambda parameter name
  is the builder identifier for that scope; nested `OwnsOne`/`OwnsMany` introduce `b1`, `b2`, … and
  their own entity. **Track the scope by the lambda parameter symbol, not by indentation.**
- Inside a scope, match on the invocation's method name: `Property<T>`, `IsRequired`,
  `HasMaxLength`, `HasColumnType`, `HasColumnName`, `HasDefaultValue`, `HasDefaultValueSql`,
  `ValueGeneratedOnAdd`/`OnAddOrUpdate`, `HasKey`, `HasAlternateKey`, `HasIndex`, `IsUnique`,
  `HasDatabaseName`/`HasName`, `ToTable`, `ToView`, `HasBaseType`, `HasDiscriminator`, `HasValue`,
  `HasOne`, `HasMany`, `WithOne`, `WithMany`, `HasForeignKey`, `HasPrincipalKey`, `OnDelete`,
  `Navigation`, `OwnsOne`, `OwnsMany`.
- Anything unrecognised is skipped silently. `snapshot-future.txt` is the test for this.
- Cardinality: `HasOne`+`WithMany` → `OneToMany`; `HasOne`+`WithOne` → `OneToOne`;
  `HasMany`+`WithMany` → `ManyToMany`. Ownership (`OwnsOne`/`OwnsMany`) sets `IsOwnership`.
- Implicit join entity detection: an entity with no `.` in its name, exactly two FKs, and a composite
  key spanning both. Flag `IsImplicitJoin`; collapsing it is a view concern, not a parse concern.
- Shadow FK candidates: a property that is an FK, has no matching navigation, and is not in the CLR
  type's own property list — the snapshot cannot prove this, so mark it as a candidate and let the UI
  say "likely shadow" rather than asserting.
- `SourceHash` = SHA-256 of the file bytes, hex. Used for staleness in D4.

**Tests.** Parse every fixture and assert on entity counts, property flags, key composition, index
names, FK endpoints and delete behaviours, owned-type nesting, TPH base/discriminator, and the
many-to-many join collapse flag. Assert `snapshot-wrapped-args.txt` produces the same FK as its
unwrapped equivalent. Assert `snapshot-future.txt` parses without throwing and still returns entities.

---

## 6. Phase D2 — Core: views, layout, scene (2 days)

**`DiagramNodeContent.Build`.** Applies `DiagramKind` and the view options to produce nodes and edges.
The ER/class differences are the table in `DIAGRAMS-PLAN.md` §4 and they live only here.

**`DiagramLayout.Compute`.** Hand-rolled layered layout:

1. Size each node: `MeasureText` over the title and visible rows, plus padding. Clamp width to a
   min/max so one long column type cannot make a node 900px wide (ellipsise instead).
2. Rank by dependency depth — principals before dependents. Break cycles by ignoring back-edges on a
   DFS; self-references are an edge from a node to itself and get their own small loop route.
3. Order within a rank by barycentre of connected nodes in the previous rank, two passes. Good enough
   is genuinely good enough — manual dragging is a shipped feature, so auto-layout only has to be a
   sane starting point.
4. Position ranks with a configurable gutter, each rank centred against the longest — as columns
   running left to right, or as rows running top to bottom, per `LayoutOptions.Flow`.
5. Route edges orthogonally: exit the dependent's facing edge, one segment across the gutter, enter
   the principal's facing edge. Stagger the crossing segment per edge so parallel edges do not
   overlap. `SceneBuilder.EndMarker` reads its direction off the route's own last segment, so it
   needs no separate say in which way the layout runs.
6. Deterministic: same input, same output, no `Random`, no dictionary-order dependence. Order
   entities by name before ranking so the layout does not shuffle between runs.

**`SceneBuilder.Build`.** Turns the layout plus `SceneState` (selected node, highlighted set, dimmed
set) into `DiagramShape`s with roles. Includes cardinality markers (crow's foot or `1`/`*` labels),
delete-behaviour labels when enabled, and PK/FK/AK/IX badges.

**Tests.** Given a fixed `MeasureText` stub: assert no two node rects overlap; assert every edge's
endpoints lie on the correct node borders; assert determinism by computing twice and comparing;
assert a cyclic model terminates; assert the ER and class views of the same model produce different
row text but the same node count (modulo collapsed join entities).

---

## 7. Phase D3 — The tab and the surface (2 days)

**`DiagramsViewModel`.** Follows `ScriptViewModel` exactly: constructor takes
`(CommandSession session, Func<EfTarget?> target, Action persist, DisplaySettings display)`;
view-supplied `Func`s for save/folder/open/reveal dialogs; `Restore`/`Store`/`Clear`;
`NotifyTargetChanged()`; `OnActivatedAsync()`.

Commands: `Generate`, `SwitchView`, `ToggleLock`, `ReLayout` (confirmed — it discards dragging),
`FitToWindow`, `ZoomIn`/`ZoomOut`/`ResetZoom`, `ExportJson`/`Png`/`Svg`/`Pdf`/`Mermaid`,
`CopyMermaid`, `NextMatch`.

`Generate` goes through `Session.RunLocalAsync("Generating diagram", ct => …)`: locate → read →
parse → build → layout → scene, checking the token between stages. On no snapshot found, set the
empty-state message and return. Nothing auto-generates on tab activation or context change, matching
the `DiscoveryMode.Cached` philosophy already in `Settings.cs`.

**`DiagramSurface`.** A custom `Control`:

- `Scene` styled property; `Render(DrawingContext)` replays it, resolving roles through
  `DiagramTheme`.
- Pan/zoom via a `Matrix`. `PointerWheelChanged` scales about the cursor; `PointerPressed`/`Moved`/
  `Released` pans. **No `Avalonia.Controls.PanAndZoom` dependency** — it is deprecated on NuGet,
  targets Avalonia 11 only, and this app is on 12.1.1. ~80 lines here instead.
- Hit-test: transform the pointer into scene space and linear-scan the node rects. Under 100 nodes,
  a loop is the right answer.
- Drag: when unlocked, pointer-drag on a node updates its position and raises an event the view model
  persists. Locked is the default.
- Keyboard: `+`/`-` zoom, arrows pan, `Ctrl+0` reset, `F` fit, `Esc` clear selection.

**Detail panel.** A pane beside the surface, bound to the selected `DiagramEntity`: CLR type,
namespace, table, schema, base type and discriminator, every property with column name, column type,
nullability, max length, default and key role, every index, and inbound/outbound relationships with
their delete behaviour. All already extracted — there is real detail to show, which was the open
question in the brief.

**Search.** A text box filtering on entity name, table name, property name, column name and index
name. Matches highlight, non-matches dim, `NextMatch` centres the view on the next one.

**Staleness badge.** Two independent signals, worded differently:

- *"This diagram was built from an older snapshot"* — `SourceHash` no longer matches the file (D4).
- *"Your code has changed since the last migration, so this diagram is behind it"* — from
  `migrations has-pending-model-changes`. Reuse `ToolsViewModel`'s existing invocation rather than
  adding a second one; it costs a build, so run it on explicit request, not on every generate.

**Empty states.** No workspace → the same message the other tabs use. No context selected → prompt.
No snapshot for this context → *"`AuditContext` has no migrations, so there is no model snapshot to
draw. Add a migration on the Migrations tab first."*

---

## 8. Phase D4 — Persistence (½ day)

`DiagramStore`:

```
Path(root, workspacePath, contextName)
    -> <root>/diagrams/<SettingsStore.WorkspaceKey(workspacePath)>/<safe-context-name>.json

SavedDiagram
    Model      DiagramModel
    Positions  Dictionary<string, PointD>   // entity name -> position
    Locked     bool
    Options    DiagramViewOptions
    Kind       DiagramKind
```

- AppData, not temp — unlike `MigrationFiles.ScriptCachePath`, this data is meant to survive. Write
  atomically and swallow a corrupt file the same way `SettingsStore.ReadOrDefault` does: a corrupt
  diagram means "regenerate", never a crash.
- **Staleness:** on load, re-hash the snapshot at `Model.SourcePath`. A different hash, or a missing
  file, badges the diagram out of date but keeps showing it — same principle as
  `ScriptViewModel.IsStale`. A stale diagram is still readable; it just stops looking current.
- **Layout preservation across regeneration:** match old positions to new nodes by entity name. New
  entities get auto-placed in the layout; vanished ones are dropped. Regenerating after adding one
  entity must not scramble a hand-arranged diagram.
- The small pointers that go in `WorkspaceSettings` rather than the diagram file: last `DiagramKind`,
  the view options, and the lock flag, so the tab opens as it was left even before the diagram loads.

**Tests.** Round-trip a `SavedDiagram`. Assert a corrupt file yields null and no throw. Assert a
changed source hash sets the stale flag. Assert positions survive a regeneration that adds and
removes an entity.

---

## 9. Phase D5 — Export (1 day)

One replay function per backend, over the same `DiagramScene`.

| Format | Implementation |
| --- | --- |
| JSON | `JsonSerializer.Serialize(model)` with the existing `SettingsStore`-style options (indented, enums as names). The `DiagramModel`, not the scene — the model is the useful artefact. |
| SVG | `SvgWriter`. Hand-written: `<rect>`, `<line>`, `<polyline>`, `<text>`, one `<g id="Entity_Blog">` per node so the file is inspectable and styleable. Escape text. Embed a `<style>` block from the palette so colours are editable in one place. |
| PNG | `SKSurface.Create` at the diagram's full size × a scale factor (1×/2×/4× picker), replay, `SKImage.Encode`. |
| PDF | `SKDocument.CreatePdf`, `BeginPage(width, height)` sized to the diagram, replay, `EndPage`. One page — no tiling. |
| Mermaid | `MermaidWriter`. `erDiagram` for the ER view, `classDiagram` for the class view. Also a Copy-to-clipboard command, since pasting into a PR description is the main use. |

Destination handling is `ScriptViewModel`'s, reused wholesale: an optional configured output folder,
a Save As dialog when there is none, `LastSaveAsFolder`, a suggested filename, plus Open file and
Open folder. That path is already built, already tested and already familiar.

Palette for export: default to the current theme's colours, with a "always export light" option —
a dark-theme diagram pasted into a document is usually not what was wanted.

**Tests.** `SvgWriter` output parses as XML, contains one group per entity, and escapes a `<` in an
entity name. `MermaidWriter` output matches a golden file for `snapshot-rich.txt`, in both kinds.
PNG/PDF get one smoke test each: non-empty output, plausible header bytes. No pixel comparisons.

---

## 10. Phase D6 — Polish and docs (½ day)

- `SettingsWindow` entry for `DefaultDiagramKind`.
- Tooltips on every toggle, and hint text saying the diagram reflects the mapped relational model as
  of the last migration.
- `PROGRESS.md`: record the phase, correct the Core-has-no-packages wording.
- `ROADMAP.md`: park MSAGL layout, per-migration diagram diffing, cross-context diagrams, diagram
  editing.
- `README.md`: one line and a screenshot.

---

## 11. Order and estimate

| Phase | Deliverable | Estimate |
| --- | --- | --- |
| D0 | Spike: rich sample, fixtures, fluent-call inventory | ½ day |
| D1 | Core: locator + parser + model | 1½ days |
| D2 | Core: views, layout, scene | 2 days |
| D3 | Tab, surface, interaction, detail panel, search, badges | 2 days |
| D4 | Persistence and staleness | ½ day |
| D5 | Export: JSON, SVG, PNG, PDF, Mermaid | 1 day |
| D6 | Settings, docs, roadmap | ½ day |
| | | **~8 days** |

D0–D3 is the first thing worth showing anyone. **D4 must not slip past D5** — an export button on a
diagram you have to regenerate every session is the wrong order to build in, and "savable between
sessions" is an explicit requirement.

---

## 12. Risks

| Risk | Mitigation |
| --- | --- |
| Snapshot format changes in a future EF version | Parser skips unknown calls rather than throwing, and `snapshot-future.txt` tests exactly that. Keep one fixture per EF major version as they appear. |
| Roslyn package size shows up in the published single-file output | Measure at D1. `Microsoft.CodeAnalysis.CSharp` is a few MB; if it materially moves the installer, the fallback is trimming configuration, not a hand-written parser. |
| Hand-rolled layout looks bad on a real 80-entity model | Manual dragging plus Re-layout is the escape hatch from day one. MSAGL is the parked upgrade, and swapping it in only touches `DiagramLayout.Compute`. |
| Node sizing depends on the real font, which Core cannot see | The `MeasureText` delegate. Tests use a stub; the app uses `FormattedText`. Deliberately a knob rather than a constant. |
| Scene colours baked at build time would not repaint on a theme switch | Roles, not colours (§1). This is the same failure the SQL `.xshd` files hit, recorded in `ROADMAP.md`. |
| Adding a tab shifts every tab index | The `SelectedTab` enum, done in D3 before the tab is added. |
| `Avalonia.Controls.PanAndZoom` looks like an easy win | It is deprecated and Avalonia-11-only. Do not add it. ~80 lines of matrix maths instead. |
| Touching `samples/SampleEfApp` breaks 300-odd passing tests | Do not touch it. `samples/SampleRichModel` is a new project. §3. |

---

## 13. Definition of done

- `dotnet test EfAssist.slnx` green, with new tests for the parser, layout, writers, store and view
  model.
- Diagrams tab: generate, cancel mid-generation, pan, zoom, fit, select a node and read its metadata,
  drag when unlocked, search and highlight, toggle property categories, switch between ER and class.
- Close the app, reopen it, reopen the same workspace and context: the diagram and its hand-arranged
  layout come back without regenerating.
- A context with no migrations shows the empty state, not an error.
- Editing an entity and adding a migration, then reopening the tab: the stale badge appears.
- All five exports produce a file that opens correctly in a third-party viewer.
- `PROGRESS.md` and `ROADMAP.md` updated.
