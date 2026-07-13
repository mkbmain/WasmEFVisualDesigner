# Read-only ER Diagram Render — Design

## Problem

The Blazor shell (`2026-07-13-blazor-wasm-shell-design.md`) proved
`EntityClassParser` runs correctly under Mono WASM, but its demo page only
dumps parse results as plain text. The backlog (`docs/backlog.md`,
Priority 4) calls for an actual "read-only ER diagram render of parsed
`EntityModel`s" as the next slice, flagged with an open risk: no Blazor
canvas/diagramming library had been chosen yet.

The user's stated long-term goal is a WYSIWYG drag-and-drop editor similar
to SSMS's table designer. That goal shapes the library choice now, even
though this slice itself is read-only: picking a library capable of
drag/connect interactions today avoids redoing the rendering layer when
editing is built later.

## Goal

Render parsed entities and their relationships as a visual diagram —
entity boxes with property lists, connected by relationship lines — in the
existing Blazor WASM shell, using a real diagramming library rather than a
text dump.

## Non-goals (this slice)

- No drag persistence or any diagram editing — nodes may be draggable by
  virtue of the library's default behavior, but moved positions are not
  saved or fed back into any model.
- No `.zip` / file upload — input stays pasted text, per the shell's
  existing pattern.
- No auto-layout algorithm — node placement is a simple deterministic grid.
- No wiring to `OnModelCreatingRewriter` / `EntityClassRewriter` — still
  read-only end to end.
- No styling polish beyond the diagramming library's defaults.
- No automated test project for the Web app (consistent with the shell
  slice — Web stays a thin, already-tested-`Core`-calling layer).

Each remains a separate, already-tracked backlog item (editable diagram,
`.zip` upload, GitHub Actions deploy).

## Library choice

**Z.Blazor.Diagrams** (GitHub: `Blazor-Diagrams/Blazor.Diagrams`; NuGet:
`Z.Blazor.Diagrams` + `Z.Blazor.Diagrams.Core`).

Rejected alternatives:
- **Syncfusion Blazor Diagram**, **MindFusion Diagramming for Blazor** —
  both commercial/paid. The original design spec (`2026-07-07-...`) commits
  to the tool staying "free and fully open source... not up for trade" as
  part of its trust story; a paid dependency contradicts that even if the
  library itself is optional to license for small use.
- **Hand-rolled SVG** — would require building drag/hit-testing/connection
  routing from scratch, work that's thrown away once real editing
  (the WYSIWYG goal) is built. Z.Blazor.Diagrams already provides
  draggable nodes and connectable links.
- **JS library via interop** (mermaid.js, cytoscape.js, jsPlumb) — best
  layout quality, but forces JSInterop across the entire future editing
  lifecycle (every drag/connect event needs to flow C# ↔ JS state).
  Z.Blazor.Diagrams is ~95% C#, using JS only for bounds/observers, keeping
  the stack consistent with the rest of the project.

## Architecture

- Add `Z.Blazor.Diagrams` + `Z.Blazor.Diagrams.Core` package references to
  `EfSchemaVisualizer.Web`.
- New orchestration type in the Web project, `DiagramModelBuilder` (not
  added to `Core`): given entity-class source and DbContext/config source,
  runs the full parse → merge pipeline and returns
  `(IReadOnlyList<EntityModel> Entities, IReadOnlyList<RelationshipModel>
  Relationships, IReadOnlyList<Diagnostic> Diagnostics)`.

  This orchestration deliberately lives in the Web project, not `Core`:
  every prior slice (add/drop/rename property, add/remove entity, etc.)
  kept parse/merge/rewrite as separate composed calls rather than building
  one orchestrating method in `Core` (see backlog Priority 1 entries). This
  follows the same precedent — `Core` stays a library of composable steps;
  a UI-facing caller does the composing.

  Steps inside `DiagramModelBuilder`:
  1. `EntityClassParser.Parse(classSource)` → base `EntityModel` list +
     diagnostics.
  2. Run all ten `FluentConfigParser.Parse*` methods against
     `configSource` → the ten config-list types + diagnostics.
  3. For each entity, fold in every applicable `ModelMerger.Apply*` (max
     length, required, precision, keys, indexes, table mapping, column
     name/type, default value) — same nine calls the existing rewriter
     tests already exercise, just composed for read purposes instead of
     write.
  4. `ModelMerger.ApplyRelationships(relationshipConfigs)` →
     `RelationshipModel` list, independent of any single entity.
  5. Concatenate diagnostics from steps 1–2 for display.

- A `Z.Blazor.Diagrams.Diagram` instance is built from the result:
  - One node per `EntityModel`, using a custom node component
    (`EntityNode.razor`) rendering the entity name as a title bar and each
    `PropertyModel` as a row (`Name: ClrType` + `?` if nullable). Key
    properties (from `KeyPropertyNames`) are visually marked (e.g. bold or
    a key icon prefix) — no other property metadata (column name/type,
    default value, precision) is surfaced in this slice; the node stays
    name+type+nullable+key to match the "class diagram" reference point
    the user approved, and avoid a cluttered first pass.
  - One `LinkModel` per `RelationshipModel`, connecting the port on the
    `DependentEntity` node to the port on the `PrincipalEntity` node.
    `RelationshipKind` (one-to-many, one-to-one, many-to-many) is rendered
    as a label on the link (e.g. "1—*") — no crow's-foot notation or other
    custom arrowheads in this slice, since Z.Blazor.Diagrams links support
    text labels out of the box and that's sufficient to distinguish kinds.
  - Node placement: a fixed-column grid (e.g. 4 columns), position `i`-th
    entity at `(col * xSpacing, row * ySpacing)` in creation order. No
    attempt to minimize link crossings or group related entities — this is
    explicitly deferred to a future auto-layout slice.

## Page

Extends the existing `Home.razor` demo (same page, not a new route, since
the shell's single-page scope hasn't been revisited):

- Two `<textarea>` inputs: "Entity classes" (pre-filled with a small
  two-entity + relationship sample) and "DbContext / OnModelCreating"
  (pre-filled with matching fluent config for that sample).
- A "Render Diagram" button. On click, runs `DiagramModelBuilder`, then:
  - If diagnostics exist, renders them above the diagram (reusing the
    existing diagnostics list rendering from the shell slice).
  - Builds the `Diagram` and renders it via Z.Blazor.Diagrams'
    `<CascadingValue>`/`<DiagramCanvas>` component below.
- The old single-textarea "Parse" demo is replaced by this two-textarea
  flow — the shell's original demo already proved the WASM/Roslyn risk;
  this slice supersedes it as the page's purpose.

## Data flow

Two textareas → `DiagramModelBuilder` (client-side, in-WASM, no network
call) → `(EntityModel[], RelationshipModel[], Diagnostic[])` → mapped into
a `Z.Blazor.Diagrams.Diagram` (nodes + links) → rendered by
`DiagramCanvas`. No server round-trip, consistent with the project's
stateless, client-only trust model.

## Error handling

Same posture as the shell slice: parsing/merging is wrapped so an
exception is caught and displayed inline rather than crashing the page.
This remains a functional slice, not hardened error handling.

## Verification

Manual, not automated (same rationale as the shell slice — Web is a thin
caller into already-tested `Core` logic, and Z.Blazor.Diagrams' own
rendering isn't something a unit test meaningfully covers without a real
browser):

1. `dotnet publish`, serve locally, open in a browser.
2. Confirm the pre-filled sample (two related entities) renders as two
   node boxes, each listing its properties with types, connected by a
   labeled relationship line.
3. Confirm key properties are visually distinguished from non-key
   properties on a node with a composite or single key configured.
4. Edit one textarea to introduce a parse error (e.g. malformed fluent
   config) and confirm diagnostics render above the diagram without
   crashing the page, and that the diagram still renders using whatever
   did parse successfully.
5. Confirm nodes can be dragged around the canvas (default library
   behavior) without the page erroring — even though this slice does not
   persist positions, dragging should not break rendering, since it's the
   foundation the WYSIWYG goal builds on next.
6. Result (recorded 2026-07-13): `dotnet publish -c Release` succeeded.
   Published `_framework` payload was 46M (`du -sh`; 47,305,557 bytes via
   `du -sb`) — indistinguishable at this rounding from the shell slice's
   46M baseline, so Z.Blazor.Diagrams itself adds negligible weight on top
   of the Mono/Roslyn runtime, which dominates the payload. The
   `_content/Z.Blazor.Diagrams/` static assets (`style.min.css`,
   `script.min.js`, and their `.br`/`.gz` variants) were confirmed present
   in the publish output, so the package's content files are wired into
   the build correctly.

   Interactive browser verification (page load timing, diagram rendering
   of the two entity nodes with property lists and key marking, the
   labeled relationship line, node dragging, diagnostics-on-parse-error
   behavior) was **not performed** — this sandbox environment has no
   browser, no Node.js, and no Playwright available, so there is no way to
   drive a browser and observe actual rendering here. This remains an open
   item: a future session (or the user, manually) needs to `dotnet
   publish`, serve `wwwroot` locally (e.g. `python3 -m http.server`), open
   it in a real browser, and confirm the six behaviors listed under
   "Verification" step 2 above before this slice's core risk (does
   Z.Blazor.Diagrams actually render and stay interactive under Mono WASM)
   is considered resolved.

## Open risk this slice resolves

Confirms Z.Blazor.Diagrams renders and remains interactive (drag, pan,
zoom) correctly under Mono WASM in a real browser, and gives a first data
point on how much the diagramming library adds to payload size on top of
the Roslyn baseline. If the library fails under WASM or adds prohibitive
weight, the library choice needs revisiting before the editable-diagram
slice builds further on top of it.
