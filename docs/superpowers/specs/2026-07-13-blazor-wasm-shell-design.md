# Blazor WebAssembly Shell — Design

## Problem

The original design spec (`2026-07-07-ef-schema-visualizer-design.md`) calls
for a Blazor WebAssembly app referencing `EfSchemaVisualizer.Core`. That
spec's biggest flagged risk is unproven: Roslyn's syntax-tree APIs have never
actually been exercised inside a browser under Mono WASM. Everything else
planned for the application shell — file upload, diagram rendering, editing,
CI deploy — depends on that working. This is the first opportunity to check
it before investing in anything built on top.

The backlog (`docs/backlog.md`, Priority 4) lists the shell as its own item,
separate from the read-only diagram render, the editable diagram, `.zip`
upload/download, and the GitHub Actions deploy pipeline. This design scopes
only the shell.

## Goal

Stand up a minimal, working Blazor WebAssembly project that:

1. References `EfSchemaVisualizer.Core` as a project reference.
2. Runs in a real browser (via `dotnet publish` + local static serving).
3. Proves `EntityClassParser` (Core's Roslyn-based syntax parser) executes
   correctly under Mono WASM, by actually calling it at runtime against
   user-supplied input — not just compiling the reference.

## Non-goals (this slice)

- No file or `.zip` upload — input is pasted/typed text in a textarea.
- No ER diagram rendering.
- No editing/rewrite functionality (`OnModelCreatingRewriter`,
  `EntityClassRewriter` etc. are not wired up here).
- No GitHub Actions / GitHub Pages deploy pipeline.
- No styling or UX polish — this is a functional proof, not a product page.
- No automated test project for the Web app.

Each of the above remains a separate, already-tracked backlog item.

## Architecture

- New project: `src/EfSchemaVisualizer.Web`, a standalone Blazor
  WebAssembly project (net10.0, no ASP.NET Core hosting project — matches
  the "fully static, no backend" architecture from the original spec).
- Project reference to `EfSchemaVisualizer.Core`.
- Added to `EfSchemaVisualizer.slnx`.

## Page

A single page (the default `Home.razor`, repurposed) containing:

- A `<textarea>` pre-filled with a small sample entity class, so there's
  working input the moment the page loads.
- A "Parse" button. On click, the pasted text is run through
  `EntityClassParser.Parse` synchronously, in-browser (WASM, no network
  call).
- The resulting `ParseResult<EntityModel>` — including the entity name,
  properties, and any diagnostics — rendered below the button as plain
  formatted text (manual field dump). No JSON serialization, no styling
  investment; this only needs to be legible enough to confirm correctness
  by eye.

If parsing throws (e.g. malformed input the parser doesn't yet guard
against), the exception message is caught and displayed inline rather than
crashing the page — this is a proof-of-concept safety net, not a hardened
error-handling design.

## Data flow

Textarea text → `EntityClassParser.Parse(string)` (runs entirely
client-side, in the WASM sandbox) → `ParseResult<EntityModel>` → rendered
to the page. No server round-trip at any point, consistent with the
project's stateless, client-only trust model.

## Verification

Manual, not automated:

1. `dotnet publish` the Web project.
2. Serve the published `wwwroot` output locally (e.g. `dotnet serve` or
   equivalent static server).
3. Open in a browser, confirm the page loads and the sample class parses
   without modification.
4. Edit the textarea to a different/malformed class and confirm the output
   updates (or the error path renders) without a page crash.
5. Record the published payload size and a rough first-load time in this
   design doc as the result of the Roslyn-in-WASM risk check (filled in
   during implementation).

No bUnit or other automated test project is added for this slice — there's
no business logic in the Web project itself yet (it's a thin caller into
already-tested `Core` code), and the project's style favors lean,
spike-first slices over upfront test scaffolding where the risk doesn't
warrant it.

## Open risk this slice resolves

Confirms (or refutes) whether Roslyn's `CSharpSyntaxTree.ParseText` /
`CSharpSyntaxWalker` APIs work correctly under Mono WASM in a real browser,
and gives a first real data point on payload size / load time — both
flagged as unresolved risks in the original design spec. If this fails or
the payload is prohibitively large, later slices (diagram rendering,
editing) need to be re-planned before proceeding.
