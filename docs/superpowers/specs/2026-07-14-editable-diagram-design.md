# Editable ER Diagram (WYSIWYG) — Design

## Problem

The read-only ER diagram (`2026-07-13-er-diagram-render-design.md`) renders
entities and relationships but cannot be edited — the backlog's Priority 4
"Editable diagram wired to the rewriter" item. The master spec's stated goal
(`2026-07-07-ef-schema-visualizer-design.md`, Goal §3) is direct-manipulation
editing "similar to SQL Server Management Studio's table designer — not a
form-based editor bolted onto a static render." `Core` already has parse,
merge, and rewrite support for nearly every fluent config surface (max
length, precision, required, keys, indexes, table/column mapping, default
values, add/remove/rename entity and property) built up across the Priority
1–3 backlog items — this design wires the diagram UI to that existing
rewriter surface, and fills the two gaps that don't exist yet (property type
changes, relationship write-back).

This is a large feature. It is specified here as one design so every phase
shares one architecture and one set of UX conventions, but is implemented as
five separate, independently executable plans (see Sequencing) — consistent
with how every prior backlog item shipped as its own small slice.

## Goal

Let a user edit the diagram directly — rename entities/properties, change a
property's type, add/remove entities and properties, toggle keys, manage
indexes, edit table/column mapping and default values, and draw/delete
relationships — with every action immediately reflected in regenerated C#
source, shown back in the same two textareas the user pasted their source
into.

## Non-goals

- **`.zip` upload/download** — separate, not-yet-built backlog item. Source
  stays as pasted text in the two textareas; regenerated text is written
  back into those same textareas, not offered as a file download.
- **Auto-layout** — new entities/nodes still place via the existing
  fixed-grid/default-position logic; no crossing-minimization or grouping.
- **Undo/redo** — out of scope. The textareas remain plain editable text, so
  a user can hand-edit or paste over a mistake; no separate undo stack.
- **Multi-file / namespace-aware editing, `IEntityTypeConfiguration<T>`
  write-back** — parse+merge already supports `IEntityTypeConfiguration<T>`
  reading; editing still targets the `OnModelCreating` fluent style the
  rewriter already writes. Config classes stay read-only for now (matches
  the precedent set when relationships were parse-only: rewriter deferred).
- **Concurrent/collaborative editing, server persistence** — stays fully
  client-side and single-user, consistent with the project's stateless
  trust model.
- **Data-annotation attributes** as an alternative to fluent config — out of
  scope everywhere else in this project, stays out of scope here.

## Architecture

### Source-text-is-truth on every edit

No new diagram-side edit state is introduced. Every edit gesture:

1. Validates client-side against the current parsed `EntityModel`/
   `RelationshipModel` set (see Validation below). If invalid, show an
   inline error and stop — no rewrite happens, no source changes.
2. Calls one or more `EntityClassRewriter` / `OnModelCreatingRewriter`
   methods against the current `_classSource` / `_configSource` strings
   held in `Home.razor`'s component state.
3. Writes the returned string(s) back into `_classSource`/`_configSource`.
4. Re-runs `DiagramModelBuilder.Build(_classSource, _configSource)` — the
   same call the "Render Diagram" button already makes.
5. Rebuilds the `BlazorDiagram`'s `Nodes`/`Links` from the fresh result.

This means the diagram is always a direct projection of the textarea text,
exactly as it is today for the initial render — editing just triggers steps
2–5 automatically instead of via a button click. This guarantees the
diagram and the textareas can never drift out of sync, and every edit
composes with the existing read-only pipeline instead of adding a parallel
code path.

### Preserving node positions across rebuilds

Step 5 constructs a brand-new set of `EntityNodeModel`s from the reparsed
`EntityModel` list (same as today's `RenderDiagram`). To avoid nodes
jumping back to grid position after every keystroke-triggered edit, the
rebuild step keys the *previous* node set by entity name and copies each
matched node's `Position` onto the corresponding new node before replacing
`diagram.Nodes`. A brand-new entity (no previous match) falls back to the
existing grid-placement logic. This is a small, self-contained helper
(`DiagramSync.RebuildPreservingPositions(diagram, result, columns,
xSpacing, ySpacing)`) added to the Web project in Phase 1 and reused by
every later phase — it has no dependency on which specific edit triggered
the rebuild.

### Validation before rewrite

Each edit gesture validates against the in-memory `EntityModel`/
`RelationshipModel` set already held from the last successful `Build` call,
*before* calling any rewriter method:

- **Rename** (entity or property): reject if the new name collides with an
  existing sibling name (case-sensitive, matching C# identifier rules) or
  isn't a valid C# identifier.
- **Type change**: reject if the typed text isn't a syntactically valid
  type token (parsed via `SyntaxFactory.ParseTypeName` and checked for
  parse errors — no semantic/existence check, since the tool has no
  compilation context to resolve arbitrary types against).
- **Add entity/property**: reject duplicate names, same rule as rename.
- **Relationship**: reject if the dependent entity has no valid FK
  candidate property and none can be synthesized (see Phase 5).

On rejection: an inline error renders next to the field being edited (a
small red text line, consistent with the existing `_error`/diagnostics
styling), the field reverts to its last valid value, and neither textarea
changes. This keeps the source always in a state the rewriter/parser can
round-trip, matching the "reject with inline error, no rewrite" decision.

### New Core methods

Two gaps in the existing rewriter surface, both needed before the diagram
can support the corresponding edits:

- **`EntityClassRewriter.ChangePropertyType(string sourceCode, string
  className, string propertyName, string newClrType, bool newIsNullable)`**
  — locates the property's `PropertyDeclarationSyntax` (reusing the
  existing member-lookup helpers `AddProperty`/`RemoveProperty` already
  use) and replaces its type node with a parsed `TypeSyntax` for
  `newClrType` (`?`-suffixed if `newIsNullable`), preserving the property's
  existing accessors/initializer/trivia. Follows the same "locate node,
  replace, `NormalizeWhitespace()` only if the replacement itself needed
  synthesis" pattern as `RenameProperty`.
- **`OnModelCreatingRewriter.SetRelationship(string sourceCode,
  RelationshipModel relationship)` / `RemoveRelationship(string
  sourceCode, string principalEntity, string dependentEntity)`** — the
  write-back counterpart to the existing `ParseRelationships`/
  `ApplyRelationships` read path. `SetRelationship` emits the canonical
  `HasOne(...).WithMany(...).HasForeignKey(...)` shape (matching whichever
  of the four kinds `RelationshipModel.Kind` specifies) into the
  dependent entity's configuration scope, using
  `FluentSyntaxHelpers.FindConfigurationScopes` the same way every other
  `Set*` method does. `RemoveRelationship` deletes that statement.
  Many-to-many emits `HasMany(...).WithMany(...)` without an explicit join
  entity (letting EF synthesize the shared join table) — explicit
  `UsingEntity` configuration remains out of scope, matching the
  read-path's existing non-goal.

Both are added to `EfSchemaVisualizer.Core` with unit tests following the
existing rewriter test conventions (byte-identical-where-possible,
`NormalizeWhitespace()` fallback documented same as `RewriteMaxLength`'s
precedent) — this is production `Core` logic, not Web-only glue, so it's
tested the same way every other rewriter method in this codebase is.

### Diagram UX conventions (apply across all phases)

- **Inline editing, not modals/forms**: renaming and simple field edits
  happen by clicking directly on the diagram element and editing text in
  place (contenteditable-style or a swapped-in `<input>` sized to match),
  matching the SSMS-style direct-manipulation goal.
- **Click-to-expand for secondary fields**: a property row's name, type,
  and nullable flag are always visible and directly editable; clicking the
  row expands it in place (within the same node, not a popover/modal) to
  reveal key toggle, column name/type, precision/scale, default value, and
  index membership. Collapses back on a second click or clicking elsewhere.
- **Toolbar + keyboard for structural add/remove**: a small toolbar above
  the canvas has "+ Entity"; each node has a "+ Add property" row at its
  bottom. Selecting a node or property row and pressing Delete removes it;
  a hover-revealed "×" gives a mouse-only alternative. A removed node/row
  triggers a confirmation only if it has dependent relationships that would
  also be removed (see Phase 5) — otherwise removal is immediate, since the
  textareas remain the undo mechanism.
- **Relationships via native link-drag**: Z.Blazor.Diagrams' built-in
  port-drag-to-connect gesture creates the link; no custom drag/hit-testing
  code needed (this was the stated reason the library was chosen in the
  read-only slice).

## Data flow

```
User gesture on diagram
  → validate against current EntityModel/RelationshipModel set
  → (invalid: inline error, stop)
  → (valid) Core rewriter call(s) against _classSource/_configSource
  → updated strings written back into _classSource/_configSource
  → DiagramModelBuilder.Build(_classSource, _configSource)
  → DiagramSync.RebuildPreservingPositions(...)
  → diagram re-renders
```

Identical shape for every phase; only step 3 (which rewriter method(s))
and the gesture that triggers it change per phase.

## Error handling

Same posture as the existing read-only slice: the whole gesture handler is
wrapped in try/catch; an unexpected exception (as opposed to an anticipated
validation failure) renders via the existing `_error` `<pre>` block and
leaves the textareas at their last-written value. Anticipated validation
failures use the lighter inline-error path described above, not this
catch-all.

## Verification

Same manual-verification posture as the read-only slice (Web is a thin
caller into already-tested `Core` logic; Blazor.Diagrams interaction isn't
meaningfully unit-testable without a real browser):

- **Core** (`ChangePropertyType`, `SetRelationship`, `RemoveRelationship`):
  full automated unit test coverage, following existing rewriter test
  conventions — this is the part that's actually testable headlessly.
- **Web / diagram interaction**: manual verification per phase, each
  phase's plan lists its own specific click/drag scenarios to confirm in a
  real browser (`dotnet publish`, serve `wwwroot` locally, open in a
  browser) before that phase is considered done — same gap the read-only
  slice flagged as still-open (no browser available in this sandbox).

## Sequencing

Five phases, each its own implementation plan under
`docs/superpowers/plans/`, each linking back to this design doc as shared
context. Later phases depend on earlier ones only for the shared
`DiagramSync.RebuildPreservingPositions` helper and UX conventions built in
Phase 1 — otherwise each phase's rewriter/UI work is independent and could
be reordered if priorities change, except Phase 5 which must build its two
new Core methods before any relationship-editing UX.

1. **Rename + type/nullable editing.** Smallest end-to-end slice: proves
   the validate → rewrite → rebuild → reposition loop works at all, before
   building anything riskier on top of it. Builds `ChangePropertyType` and
   `DiagramSync.RebuildPreservingPositions`.

   **Update:** Phase 1 is built. `Core` gained
   `EntityClassRewriter.ChangePropertyType` (with unit tests following the
   existing rewriter conventions). The Web project gained
   `Diagram/DiagramSync.cs` (`RebuildPreservingPositions`, the
   position-preserving diagram rebuild helper), `Diagram/DiagramEditor.cs`
   and `Diagram/DiagramEditContext.cs` (the validate → rewrite → rebuild
   orchestration), and inline rename/type/nullable editing UX added to
   `Diagram/EntityNode.razor`.

   Verification (recorded 2026-07-14): `dotnet test
   tests/EfSchemaVisualizer.Core.Tests` — 272 passed, 0 failed. `dotnet
   build` (whole solution) — `Build succeeded.`, 0 warnings, 0 errors for
   both `EfSchemaVisualizer.Core` and `EfSchemaVisualizer.Web`. `dotnet
   publish src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj -c
   Release` also succeeded, producing a working `wwwroot` output.

   Interactive browser verification (drag "Post" node, rename it to
   "Article" and confirm position/relationship-label preservation, rename a
   property, change a type, toggle nullable, and trigger the three inline
   error cases) was **not performed** — same as the read-only slice
   (`2026-07-13-er-diagram-render-design.md`), this sandbox environment has
   no browser, no Node.js, and no Playwright available (`which chromium
   chromium-browser google-chrome firefox node npx playwright` all reported
   not found), so there is no way to drive a browser and observe actual
   rendering/interaction here. This remains an open item carried forward
   from the prior slice: a future session (or the user, manually) needs to
   `dotnet publish`, serve `wwwroot` locally, open it in a real browser, and
   run through the five scenarios listed under Step 3 of the Task 8 brief
   before Phase 1's editing loop is considered fully verified end to end.
2. **Add/remove entities and properties.** Toolbar + row-level add,
   selection + Delete / hover-× remove, wired to the existing `AddClass`/
   `RemoveClass`/`AddProperty`/`RemoveProperty`.

   **Update:** Phase 2 is built. Position tracking was reworked from
   Phase 1's ordinal-matching scheme to Guid-based entity-identity
   tracking, so add/remove/rename/reorder operations can no longer
   scramble which diagram position belongs to which entity. The Web
   project's `Diagram/DiagramEditor.cs` gained `AddEntity`, `RemoveEntity`,
   `AddProperty`, and `RemoveProperty`, each guarding against unsafe
   removals (relationships, primary/foreign keys, indexes) with inline
   errors rather than silently corrupting the model. The diagram UI
   gained a "+ Entity" toolbar button (grid-placing new entities without
   overlapping existing ones) and per-node/per-property add/remove
   affordances on `Diagram/EntityNode.razor`.

   Verification (recorded 2026-07-14): `dotnet test
   tests/EfSchemaVisualizer.Core.Tests` — 277 passed, 0 failed (no new
   `Core` tests this phase, since no new `Core` methods were added — all
   new logic lives in the Web project's `DiagramEditor`). `dotnet build`
   (whole solution) — `Build succeeded.`, 0 warnings, 0 errors for both
   `EfSchemaVisualizer.Core` and `EfSchemaVisualizer.Web`. `dotnet publish
   src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj -c Release`
   also succeeded, producing a working `wwwroot` output.

   Interactive browser verification (render the sample, drag "Post", add
   two new entities and confirm non-overlapping placement and that "Post"
   keeps its dragged position, rename a new entity and confirm its
   position survives, add/remove a property, attempt to remove "Blog" and
   "Post"'s "Id"/"BlogId"/"Blog" and confirm all four are refused, and
   remove an unrelated new entity and confirm it and its `DbSet`/
   `Entity<T>()` block are fully gone) was **not performed** — same
   situation as Phase 1: this sandbox has no browser, no Node.js, and no
   Playwright available (`which chromium chromium-browser google-chrome
   firefox node npx playwright` all reported not found), so there is no
   way to drive a browser and observe actual rendering/interaction here.
   This remains an open item carried forward: a future session (or the
   user, manually) needs to serve the published `wwwroot` locally, open it
   in a real browser, and run through the six scenarios listed under Step
   3 of the Task 7 brief before Phase 2's add/remove flows are considered
   fully verified end to end.
3. **Keys and indexes.** Key-toggle on a property row; index add/remove in
   the row's expand-on-click area.

   **Update:** Phase 3 is built. The Web project's `Diagram/DiagramEditor.cs`
   gained `ToggleKey` (flips a property's primary-key membership, refusing to
   remove the last remaining key property) and five index-management
   methods — `AddIndex`, `ToggleIndexMembership`, `SetIndexUnique`,
   `RenameIndex`, and `RemoveIndex` — covering single- and composite-index
   authoring end to end. `Diagram/EntityNode.razor` gained an
   expand-on-click panel per property row (new to this codebase, introduced
   in this phase) exposing a primary-key checkbox and a full index-management
   UI: an "+ New index on this property" affordance, per-index membership
   checkboxes, a unique-toggle checkbox, an editable name field, and a
   remove ("×") button.

   Verification (recorded 2026-07-14): `dotnet test
   tests/EfSchemaVisualizer.Core.Tests` — 277 passed, 0 failed (no new
   `Core` tests this phase, since no new `Core` methods were added — all
   new logic lives in the Web project's `DiagramEditor`, consistent with
   Phase 2). `dotnet build` (whole solution) — `Build succeeded.`, 0
   warnings, 0 errors for both `EfSchemaVisualizer.Core` and
   `EfSchemaVisualizer.Web`. `dotnet publish
   src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj -c Release`
   also succeeded, producing a working `wwwroot` output.

   Interactive browser verification (expand a single-property key's row,
   confirm its "primary key" checkbox is checked, uncheck it and confirm
   the inline error and revert; add a second property, expand it, check
   "primary key" and confirm a composite `HasKey(e => new { e.X, e.Y })`
   appears with both checkboxes checked; expand a property and add a new
   index, confirming a single-property `HasIndex` call appears and the new
   row shows in every property's expanded index list; check a second
   property into that index and confirm it becomes composite
   (`HasIndex(e => new { e.A, e.B })`) then uncheck it back; toggle an
   index's unique checkbox and rename it, confirming `.IsUnique()` and the
   new name in the regenerated source; and remove an index via its "×"
   button, confirming the `HasIndex` statement is fully gone) was **not
   performed** — same situation as Phases 1 and 2: this sandbox has no
   browser, no Node.js, and no Playwright available (`which chromium
   chromium-browser google-chrome firefox node npx playwright` all
   reported not found), so there is no way to drive a browser and observe
   actual rendering/interaction here. This remains an open item carried
   forward: a future session (or the user, manually) needs to serve the
   published `wwwroot` locally, open it in a real browser, and run through
   the six scenarios listed under Step 4 of the Task 5 brief before Phase
   3's key/index editing flows are considered fully verified end to end.
4. **Column/table mapping, precision, default values.** Remaining
   expand-on-click fields, plus entity-level table/schema in the node
   header.

   **Update:** Phase 4 is built. The Web project's `Diagram/DiagramEditor.cs`
   gained `SetTableMapping` (entity-level table name/schema, emitting or
   removing `.ToTable(...)`), `SetColumnName`/`SetColumnType` (property-level
   `.HasColumnName(...)`/`.HasColumnType(...)`), and `SetPrecision`/
   `SetDefaultValue` (`.HasPrecision(N)`/`.HasPrecision(N, M)` and
   `.HasDefaultValue(...)`, the latter guarded by a new `IsValidExpressionText`
   helper mirroring the existing `IsValidTypeToken` pattern). `Diagram/
   EntityNode.razor` gained an always-visible "Table:"/"Schema:" row in the
   node header (each field committing independently via `@onchange`, no
   shared edit-mode toggle, avoiding the stale-value bug that would cause),
   plus column name, column type, precision, scale, and default-value fields
   inside the Phase-3 expand-on-click panel, each with its own inline-error
   slot reusing the existing `_propertyErrors`/`_tableError` conventions. No
   new `Core` methods were needed — this phase only wired the diagram to the
   rewriter surface `Core` already exposed.

   Verification (recorded 2026-07-14): `dotnet test
   tests/EfSchemaVisualizer.Core.Tests` — 277 passed, 0 failed (no new
   `Core` tests this phase, since no new `Core` methods were added — all new
   logic lives in the Web project's `DiagramEditor`, consistent with Phases
   2 and 3). `dotnet build` (whole solution) — `Build succeeded.`, 0
   warnings, 0 errors for both `EfSchemaVisualizer.Core` and
   `EfSchemaVisualizer.Web`. `dotnet publish
   src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj -c Release` also
   succeeded, producing a working `wwwroot` output (with a wasm-tools
   optimization advisory, not an error).

   Interactive browser verification (render a sample entity, set its table
   name and schema via the new header fields and confirm `.ToTable("Name",
   "schema")` appears, then clear the table name field and confirm the whole
   `.ToTable(...)` call disappears; expand a property, set its column name
   and column type and confirm `.HasColumnName(...)`/`.HasColumnType(...)`
   appear, then clear each back to blank and confirm they disappear; set
   precision only on a decimal property and confirm `.HasPrecision(N)` with
   one argument, add a scale and confirm it becomes `.HasPrecision(N, M)`,
   then clear precision and confirm the whole call disappears including
   scale; and set a quoted-literal default on a string property and confirm
   `.HasDefaultValue("active")` appears unchanged, set a numeric default on
   an int property and confirm `.HasDefaultValue(5)` appears, then try an
   invalid expression and confirm an inline error appears instead of
   corrupting the source) was **not performed** — same situation as Phases 1
   through 3: this sandbox has no browser, no Node.js, and no Playwright
   available (`which chromium chromium-browser google-chrome firefox node
   npx playwright` all reported not found), so there is no way to drive a
   browser and observe actual rendering/interaction here. This remains an
   open item carried forward: a future session (or the user, manually) needs
   to serve the published `wwwroot` locally, open it in a real browser, and
   run through the four scenarios listed under Step 4 of the Task 6 brief
   before Phase 4's column/table mapping editing flows are considered fully
   verified end to end.

   **Whole-phase review caught and fixed a real bug** after this entry was
   first recorded: `SetPrecision` rejected clearing precision on a
   `decimal(N,M)`-style property (Precision and Scale both set) with a
   spurious "Scale cannot be set without precision" error, because the
   Razor `CommitPrecision` handler always passes the property's still-current
   `Scale` alongside a newly-blanked precision. Fixed by making a null
   `precision` always clear the whole `.HasPrecision(...)` mapping
   unconditionally (ignoring the incoming `scale`), rather than rejecting
   the call — see commit `a5b8f85`. `dotnet build` and the traced-scenario
   reasoning in this fix's report both confirm the corrected behavior; this
   was exactly scenario 3 of the not-yet-performed browser verification
   above, so it remains important that a future browser pass exercises it
   directly.
5. **Relationships.** Builds `SetRelationship`/`RemoveRelationship` in
   Core first, then wires drag-to-connect (default one-to-many) and
   click-to-expand link-label editing (kind, FK property) in the diagram.

   **Update:** Phase 5 is built. `Core` gained
   `OnModelCreatingRewriter.SetRelationship` (emitting/rewriting the
   `HasOne`/`HasMany`/`WithOne`/`WithMany`/`HasForeignKey` chain for a
   given `RelationshipModel`) and `RemoveRelationship` (deleting that
   chain entirely), with unit tests following the existing rewriter
   conventions. The Web project's `Diagram/DiagramEditor.cs` gained
   `AddRelationship` (always creating a new `RelationshipKind.OneToMany`
   relationship between a dependent and principal entity, per this
   design's drag-from-child-to-parent convention),
   `SetRelationshipShape` (changing an existing relationship's kind and/or
   foreign-key properties), and `RemoveRelationship`. The diagram UI
   gained drag-to-connect ports on `Diagram/EntityNode.razor` (a
   `PortRenderer` on each side of the node) plus `Pages/Home.razor` wiring
   (`OnLinkAdded`/`OnRelationshipLinkAttached`, resolving the dragged
   link's source/target nodes to entity names and calling
   `AddRelationship`, discarding the in-progress link with no error if
   either end can't be resolved to an entity), and a new
   `Diagram/RelationshipLinkLabel.razor` click-to-expand panel (showing
   the "1—1"/"1—*"/"*—*" label via a new `RelationshipLabels` helper,
   expanding on click into a Kind dropdown, a foreign-key property
   dropdown, and a "Remove relationship" button).

   Verification (recorded 2026-07-14): `dotnet test
   tests/EfSchemaVisualizer.Core.Tests` — 289 passed, 0 failed. `dotnet
   build` (whole solution) — `Build succeeded.`, 0 warnings, 0 errors for
   both `EfSchemaVisualizer.Core` and `EfSchemaVisualizer.Web`. `dotnet
   publish src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj -c
   Release` also succeeded, producing a working `wwwroot` output (with the
   same wasm-tools optimization advisory as Phase 4, not an error).

   Interactive browser verification (render the sample `Blog`/`Post`
   diagram and confirm the existing relationship renders with a clickable
   "1—*" label; click the label, change Kind to "One-to-one", and confirm
   the source regenerates with `HasOne<Blog>().WithOne().HasForeignKey<Post>(...)`;
   click "Remove relationship" and confirm the `HasOne`/`HasForeignKey`
   chain disappears entirely; add a new entity, drag from one entity's
   port to another's, and confirm a new `HasOne<X>().WithMany()`
   relationship and "1—*" label appear; and drop a drag gesture on empty
   canvas and confirm the in-progress link disappears with no relationship
   created and no error) was **not performed** — same situation as Phases
   1 through 4: this sandbox has no browser, no Node.js, and no Playwright
   available (`which chromium chromium-browser google-chrome firefox node
   npx playwright` all reported not found), so there is no way to drive a
   browser and observe actual rendering/interaction here. This remains an
   open item carried forward: a future session (or the user, manually)
   needs to serve the published `wwwroot` locally, open it in a real
   browser, and run through the five scenarios listed under Step 4 of the
   Task 6 brief before Phase 5's relationship-editing flows are considered
   fully verified end to end.

   **This completes all five phases of the editable-diagram spec.** Every
   phase described in this document (rename/type/nullable editing;
   add/remove entities and properties; keys and indexes; column/table
   mapping, precision, and default values; and relationships) has now been
   built, unit-tested at the `Core` level, and verified to build and
   publish cleanly. Interactive in-browser verification remains
   unperformed for all five phases, carried forward as the one open item
   across the whole slice — see the "Known gap" note in
   `docs/backlog.md`'s editable-diagram entry.
