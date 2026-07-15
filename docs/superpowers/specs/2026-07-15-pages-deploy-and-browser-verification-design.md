# GitHub Pages Deploy + Editable-Diagram Browser Verification — Design

## Problem

Two Priority 4 backlog items remain open:

1. **`[spec]` GitHub Actions → GitHub Pages deploy on push to `main`.** Never
   built — the app has only ever been published/served manually.
2. **Interactive in-browser verification of the editable diagram.** Every
   phase of `2026-07-14-editable-diagram-design.md` (rename/type editing,
   add/remove entity+property, keys/indexes, table/column mapping,
   relationships) was implemented and unit-tested but never clicked through
   in a real browser — no browser tooling existed in the implementation
   sandbox at the time. This is called out as an open gap in
   `docs/backlog.md`'s Priority 4 section for every phase.

This design covers both, since the deploy pipeline gives us a convenient
target to verify against (the published static output is exactly what
Playwright will drive), and both are the last things standing between
Priority 4 and "done".

## Goal

1. A working `.github/workflows/deploy.yml` that builds, tests, publishes,
   and deploys the Blazor WASM app to GitHub Pages on every push to `main`,
   failing closed (no deploy) if tests fail.
2. A real, scripted browser pass through one representative gesture from each
   of the five editable-diagram phases, run against a locally published
   build, confirming the UI gesture actually produces the correct rewritten
   C# source — not just that nothing crashes.
3. Any bug the verification pass finds gets fixed as part of this work.
4. A short results note recording what was exercised and what was found,
   and `docs/backlog.md` updated to reflect the verification is now done.

## Non-goals

- No visual/UX polish pass — verification checks correctness, not styling.
- No CI-committed browser test suite. Playwright is used ad hoc in this
  session (throwaway script, not added to the repo) to match how every
  prior phase's "manual verification" was scoped — a one-time check, not an
  ongoing automated UI test harness. Standing up real Blazor UI test
  infrastructure (bUnit, Playwright-in-CI, etc.) is a separate, larger
  decision not requested here.
- Every possible gesture in every phase is not exercised — one
  representative flow per phase, per the approved scope. Exhaustive UI
  testing remains future work if wanted.
- No change to GitHub repo settings beyond what the workflow itself can do
  (enabling "Pages source: GitHub Actions" in repo settings is a manual step
  I'll flag afterward — it isn't automatable from within the repo).

## Part 1: GitHub Pages Deploy

### Workflow: `.github/workflows/deploy.yml`

Trigger: `push` to `main`. Also `workflow_dispatch` for manual re-runs.

Permissions: `contents: read`, `pages: write`, `id-token: write` (required by
`actions/deploy-pages`).

Steps:
1. `actions/checkout@v4`
2. `actions/setup-dotnet@v4` with `dotnet-version: '10.0.x'`
3. `dotnet test` across the solution — non-zero exit fails the job before
   anything is published or deployed.
4. `dotnet publish src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj -c Release -o publish`
5. Patch the base href: replace `<base href="/" />` with
   `<base href="/WasmEFVisualDesigner/" />` in
   `publish/wwwroot/index.html` via `sed`. The source file itself keeps
   `<base href="/" />` so local `dotnet run`/`dotnet publish` for local
   testing is unaffected — only the CI-published artifact is patched.
6. Create `publish/wwwroot/.nojekyll` (empty file) — GitHub Pages runs
   Jekyll by default, which excludes underscore-prefixed paths like
   Blazor's `_framework/`; `.nojekyll` disables that.
7. `actions/upload-pages-artifact@v3` with `path: publish/wwwroot`.
8. `actions/deploy-pages@v4` in a second job (`deploy`, `needs: build`,
   `environment: github-pages`) — the standard two-job split for Pages
   deploys, so the deployment URL is exposed via `github-pages` environment
   the same way `actions/deploy-pages` expects.

### Manual step (outside this change)

After merging, GitHub repo Settings → Pages → Source must be set to "GitHub
Actions" (currently unset/disabled, since Pages was never configured). I'll
call this out explicitly when the workflow is merged; I can't change repo
settings myself.

## Part 2: Editable-Diagram Browser Verification

### Tooling

Node.js + Playwright (Chromium) installed ad hoc in this sandbox (root +
network access confirmed available). Not added to the repo — a throwaway
script in the scratchpad directory, deleted or left there at session end,
matching the non-goal above.

### Procedure

1. `dotnet publish -c Release` the Web app locally; serve `publish/wwwroot`
   statically on localhost (e.g. `dotnet-serve` or Python's
   `http.server` — whichever is available/simplest, doesn't matter which).
2. Launch Chromium via Playwright, navigate to the served app.
3. Load the app's existing shipped sample data (the two textareas'
   pre-filled defaults, per `Home.razor`) so there's a real entity/config
   pair to manipulate, and run the initial parse so the diagram renders.
4. Exercise one gesture per phase, reading the regenerated source textarea
   after each to confirm the rewrite is correct (not just that the UI
   updated):
   - **Phase 1:** Double-click an entity name to rename it; confirm the
     class declaration, any `Entity<T>()`/`DbSet<T>` references, and any
     navigation-property type references elsewhere all update.
   - **Phase 2:** Add a new property via the node's "+ Add property" row;
     remove an existing property via its "×" button; confirm both the POCO
     member and (for the removed one) any of its fluent config statements
     are gone.
   - **Phase 3:** Toggle a property's primary-key flag via the expand panel;
     confirm `HasKey(...)` is written/updated in the config source.
   - **Phase 4:** Set a table name on an entity via the node header field;
     confirm `ToTable(...)` appears in the config source.
   - **Phase 5:** Drag from one entity's connection port to another to
     create a relationship; confirm a `HasOne`/`WithMany` (or equivalent)
     pair appears in the config source with the correct types.
5. Screenshot each step as evidence.
6. Any mismatch between the UI gesture and the resulting source is a real
   bug — triage and fix it in `Core`/`DiagramEditor`/the Razor components as
   appropriate, then re-verify that specific gesture.

### Output

- `docs/superpowers/specs/2026-07-15-editable-diagram-browser-verification.md`
  — results note: what was exercised, screenshots (or a description if
  screenshots aren't practical to embed), any bugs found and how they were
  fixed.
- `docs/backlog.md` Priority 4 updated: the "Known gap across all merged
  phases" note under the editable-diagram item is replaced with a summary
  pointing at the new results note.

## Testing

- Deploy workflow: cannot be fully tested without pushing to `main` and
  observing a real Pages deployment (no local GitHub Actions runner in this
  environment). I'll validate what's checkable locally — `dotnet test` and
  `dotnet publish` succeed, the `sed` patch produces the expected
  `index.html`, `.nojekyll` is created — and rely on the first real push to
  confirm the deploy step itself. If it fails, it's a fast, cheap fix-forward
  (re-push after adjusting the workflow).
- Browser verification: the verification *is* the test — see Procedure above.
