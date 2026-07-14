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
2. **Add/remove entities and properties.** Toolbar + row-level add,
   selection + Delete / hover-× remove, wired to the existing `AddClass`/
   `RemoveClass`/`AddProperty`/`RemoveProperty`.
3. **Keys and indexes.** Key-toggle on a property row; index add/remove in
   the row's expand-on-click area.
4. **Column/table mapping, precision, default values.** Remaining
   expand-on-click fields, plus entity-level table/schema in the node
   header.
5. **Relationships.** Builds `SetRelationship`/`RemoveRelationship` in
   Core first, then wires drag-to-connect (default one-to-many) and
   click-to-expand link-label editing (kind, FK property) in the diagram.
