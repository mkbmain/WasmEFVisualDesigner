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
5. Result (recorded 2026-07-13): published `_framework` payload was 46M;
   first load to interactive on localhost (via headless Chromium/Playwright,
   `python3 -m http.server`) was approximately 585ms. Roslyn's syntax-tree
   APIs (`CSharpSyntaxTree.ParseText`, `EntityClassParser.Parse`) executed
   correctly under Mono WASM with no runtime errors across the
   valid-entity and no-entity cases. The malformed-input case
   (`this is not c#`) did not crash the page or throw into the `catch`
   block, matching the expected tolerant-parsing behavior — but it also did
   not exercise a distinct code path from the no-entity case: Roslyn parsed
   it as a syntax tree with no class/record/struct declarations, so the UI
   rendered the same `NoEntityDeclarations` diagnostic as the `IFoo`
   interface case rather than a different malformed-syntax-specific
   diagnostic. The page remained responsive (confirmed by clicking Parse
   again afterward) in all three cases.

No bUnit or other automated test project is added for this slice — there's
no business logic in the Web project itself yet (it's a thin caller into
already-tested `Core` code), and the project's style favors lean,
spike-first slices over upfront test scaffolding where the risk doesn't
warrant it.

### Re-measurement (2026-07-15, post editable-diagram + zip upload/download)

Re-ran the payload measurement (`docs/backlog.md` Priority 4's "Roslyn WASM
payload size / first-load time" item) against the app as it stands today —
full diagram editing (all 5 phases), zip upload/download, `Z.Blazor.Diagrams`
— to see how far the payload has moved since the single-textarea 2026-07-13
spike.

**Method:** `dotnet publish -c Release`, then measured the published
`wwwroot` directly (byte counts via `os.path.getsize`, not `du` block
rounding). No in-browser timing was possible — this sandbox still has no
browser or headless-Chromium binary available (same standing gap recorded
for every UI-verification step since; see `docs/backlog.md`'s "Known gap
across all merged phases" note under the editable-diagram entry).

**Results:**
- Published `wwwroot`: 58.9 MB raw. `_framework/` (all files, including the
  13 locale satellite-resource subfolders): 47.5 MB raw — essentially
  unchanged from the 2026-07-13 spike's 46 MB, despite the app growing from
  one textarea to the full diagram editor, five rewriter phases, and zip
  import/export. The fixed cost of `Microsoft.CodeAnalysis` +
  `Microsoft.CodeAnalysis.CSharp` + the Mono/BCL runtime dominates; the
  app's own code (`EfSchemaVisualizer.Core.wasm`,
  `EfSchemaVisualizer.Web.wasm`, `Blazor.Diagrams*.wasm`,
  `SvgPathProperties.wasm`) is a small fraction of the total.
- The locale satellite-resource folders (`cs/`, `de/`, `es/`, `fr/`, `it/`,
  `ja/`, `ko/`, `pl/`, `pt-BR/`, `ru/`, `tr/`, `zh-Hans/`, `zh-Hant/`) are
  lazy-loaded by the Blazor runtime only if the user's culture needs them —
  an `en-US` session downloads none of them. Excluding those, the top-level
  `_framework/` assets a first load actually needs total **22.6 MB raw**.
- Blazor's publish step already emits pre-compressed `.br` (Brotli) and
  `.gz` siblings for every asset. Taking the best available compression per
  asset, the realistic first-load transfer size is **~6.6 MB** — *if* the
  hosting layer serves the precompressed variant via content negotiation.
  GitHub Pages (the planned host, per the still-open "GitHub Actions → GitHub
  Pages deploy" backlog item) is Fastly-backed and does serve gzip
  automatically, but is not confirmed to serve the `.br` files via
  `Accept-Encoding: br` without extra configuration — this needs to be
  checked once that deploy slice exists, since it's the difference between
  a ~6.6 MB and a plausibly larger transfer.
- The publish log printed: `Publishing without optimizations. Although
  it's optional for Blazor, we strongly recommend using` `wasm-tools`
  `workload!` — the `wasm-tools` SDK workload (which enables the
  `wasm-opt`/Binaryen post-link pass and AOT options) is not installed in
  this environment. Standard IL trimming still ran (`Optimizing assemblies
  for size` did execute), but the additional wasm-specific size pass did
  not. Installing `wasm-tools` is a plausible way to shrink the
  `Microsoft.CodeAnalysis*.wasm`/`dotnet.native.wasm` files further, but
  wasn't done here since it modifies the global SDK installation — left as
  a follow-up to try, not a measurement blocker.
- No first-load-to-interactive *time* was measured this round (no browser
  available). The 2026-07-13 spike's ~585ms figure was measured on
  localhost loopback with a smaller payload and doesn't transfer to a real
  internet load; a rough transfer-only estimate off the 6.6 MB compressed
  figure is ~2s on a 25 Mbps connection or ~10s on 5 Mbps, before any
  Mono-runtime startup/JIT warm-up cost on top — but this is a napkin
  estimate, not a measurement, and should be replaced with a real
  in-browser trace once a browser is available or the GitHub Pages deploy
  exists to test against directly.

**Verdict:** the payload is not "prohibitively large" in the sense the
original open-risk note worried about — it hasn't grown materially as
features were added, and compression brings the realistic transfer to
single-digit megabytes. It is still a multi-second load on a slow
connection, worth keeping in mind for the README's framing ("first load is
slow, then it's instant and fully offline-capable") but not a blocker for
anything currently planned.

## Open risk this slice resolves

Confirms (or refutes) whether Roslyn's `CSharpSyntaxTree.ParseText` /
`CSharpSyntaxWalker` APIs work correctly under Mono WASM in a real browser,
and gives a first real data point on payload size / load time — both
flagged as unresolved risks in the original design spec. If this fails or
the payload is prohibitively large, later slices (diagram rendering,
editing) need to be re-planned before proceeding.
