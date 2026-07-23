# EF Schema Visualizer — Consolidated Backlog

> Single source of truth for everything not yet built. Combines the deferrals
> already recorded in the design spec and the core-parsing-engine plan with
> gaps discovered while reviewing the code at commit `111fedf`.
>
> **Legend for source of each item:**
> - `[spec]` — deferred in `docs/superpowers/specs/2026-07-07-ef-schema-visualizer-design.md`
> - `[plan]` — deferred in `docs/superpowers/plans/2026-07-07-core-parsing-engine.md`
> - `[found]` — discovered during code review, not previously written down
>
> **Status of what exists today (commit `111fedf`, 11/11 tests green):**
> parse entity class → `EntityModel`; parse `OnModelCreating` `HasMaxLength` →
> `MaxLengthConfig`; merge into model; surgically rewrite one existing
> `HasMaxLength` numeric literal; nested-`Entity<>` boundary isolation. Nothing
> else in the sections below is built yet.

---

## Priority 0 — Correctness & trust gaps in the *existing* engine

These are latent bugs / data-loss risks in code already written and "done".
For a tool whose pitch is "upload your real project and trust the output",
these matter before any new surface is added.

- [x] **`[found]` Parser silently drops config it doesn't understand — add a diagnostics / "unsupported" channel.**
      Today anything the parser can't model is discarded with no signal. A user
      would see an incomplete model and not know it. Introduce an
      `UnsupportedConfig` / diagnostics list on the parse result so the UI can
      warn "N fluent calls could not be read" rather than silently hiding them.
      This is the single most important trust fix.
      **Update:** `Diagnostic`/`ParseResult<T>` now populated by both
      `EntityClassParser` and `FluentConfigParser` (see
      `2026-07-08-fluent-config-parser-hardening-design.md`).
- [x] **`[found]` `EntityClassParser.Parse` throws on a class-less file.**
      `EntityClassParser.cs:15-17` does `.OfType<ClassDeclarationSyntax>().First()`.
      A file containing only an `enum`, `interface`, or `record` throws
      `InvalidOperationException`. Handle gracefully (skip / return empty) and
      support **multiple** class declarations per file (currently only the first
      is read).
- [x] **`[found]` Record and struct entities are invisible.**
      Only `ClassDeclarationSyntax` is matched. EF entities are frequently
      `record` now. Extend to `RecordDeclarationSyntax` (and consider `struct`).
- [x] **`[found]` Hardcoded `modelBuilder` receiver name.**
      `FluentSyntaxHelpers.GetConfiguredEntityName` (`FluentSyntaxHelpers.cs:82`)
      requires the identifier be literally `modelBuilder`. A renamed lambda
      param (`builder`, `b`) or config split into a helper method makes the
      whole entity vanish. Match by the `Entity<>` shape / type rather than the
      receiver's name.
- [x] **`[found]` `Property` string-overload and block-bodied lambdas not read.**
      `GetPropertyNameFor` (`FluentSyntaxHelpers.cs:53`) only handles
      `entity.Property(e => e.Name)` expression-bodied simple lambdas. The string
      overload `entity.Property("Name")` (which scaffold emits) and block-bodied
      lambdas are dropped.
- [x] **`[found]` Non-literal `HasMaxLength` arguments dropped silently.**
      `FluentConfigParser.cs:38` uses `int.TryParse(arg.ToString())`, so
      `HasMaxLength(MaxNameLength)`, `HasMaxLength(50 * 2)`, etc. are skipped.
      Inherent to syntax-only parsing, but must at minimum surface via the
      diagnostics channel above rather than disappearing.
- [x] **`[found]` No property filtering.**
      `[NotMapped]`, `static`, and get-only/computed properties all become schema
      columns. Filter to mapped instance properties.
- [x] **`[found]` Config-call chain position bug in `GetPropertyNameFor`.**
      Discovered during the final review of the `IsRequired` feature: once a
      property carries two chained fluent config calls (e.g.
      `entity.Property(e => e.Name).IsRequired().HasMaxLength(100)`), the
      shared `FluentSyntaxHelpers.GetPropertyNameFor` helper failed to
      resolve the property name for whichever call wasn't the immediate
      receiver of `Property(...)` — causing re-edits to duplicate calls
      (with EF's last-wins semantics silently discarding the user's new
      value) and causing removes to silently no-op. Fixed by walking the
      full receiver chain instead of checking only the immediate receiver;
      see `2026-07-09-is-required-config-design.md` addendum.

## Priority 1 — Editing capability the user explicitly wants (add / drop / rename)

> The user called this out directly: "we do want to add full ability to add
> properties, drop them, etc." Today the rewriter can only **mutate an existing
> literal in place** — it cannot insert or delete anything. This is the hard
> codegen work and is where the real round-trip risk lives.

- [x] **`[found]` Insert new fluent config where none exists.**
      `OnModelCreatingRewriter` can only replace an existing `HasMaxLength` arg.
      It cannot add a `HasMaxLength` (or any call) to a property that has none.
      Requires generating a new statement into a lambda body while preserving
      surrounding trivia/indentation. This is the trivia problem the spike's
      byte-identical test did *not* exercise (it only swaps one token).
      **Update:** `RewriteMaxLength` now handles all four cases (mutate,
      append, insert statement, synthesize whole block) — see
      `2026-07-08-insert-fluent-config-design.md`. Trivia is *not* preserved
      on insertion paths (whole-file `NormalizeWhitespace()` is used
      instead, a deliberate trade-off documented in the spec); only the
      pure-mutation path remains byte-identical.
- [x] **`[found]/[plan]` Add a property** to an entity (POCO class + optional config).
      **Update:** `EntityClassRewriter.AddProperty` appends a new auto-property
      to a class/record/struct's member list — see
      `2026-07-08-add-property-design.md`. Fluent config (e.g. max length)
      for the new property is a separate, composed call to
      `OnModelCreatingRewriter.RewriteMaxLength`, not orchestrated by this
      method. Record positional parameters are not supported — only body
      properties are synthesized.
- [x] **`[found]/[plan]` Drop a property** (remove from class and remove any of its config statements).
      **Update:** `EntityClassRewriter.RemoveProperty` deletes the class
      member; `OnModelCreatingRewriter.RemoveMaxLength` strips a matching
      `.HasMaxLength(...)` call, leaving the bare `Property()` call in
      place (see `2026-07-08-drop-property-design.md`). The two are
      separate composed calls, not one orchestrated operation, matching
      the `AddProperty`/config-insertion split. No config kind other than
      `HasMaxLength` exists yet to remove.
- [x] **`[plan]` Rename** an entity or property (class member + every referencing fluent call + lambda body).
      **Update:** `EntityClassRewriter.RenameClass`/`RenameProperty` rename
      the POCO side; `OnModelCreatingRewriter.RenameEntityReferences`/
      `RenamePropertyReferences` fix `Entity<T>`/`DbSet<T>` type arguments
      and `Property()` lambda/string references (see
      `2026-07-08-rename-entity-property-design.md`). Four separate
      composed calls, not one orchestrated operation, matching the
      add/drop-property split. Record positional parameters and
      free-text references outside these patterns remain out of scope.
- [x] **`[plan]` Add / remove an entity** — mint a whole new `modelBuilder.Entity<T>(...)` block, or remove one, without disturbing siblings.
      **Update:** `EntityClassRewriter.AddClass`/`RemoveClass` mint or
      delete the POCO side; `OnModelCreatingRewriter.AddEntity`/
      `RemoveEntity` mint or delete the `DbSet<T>` property and the
      `Entity<T>(...)` config block together in one pass each (see
      `2026-07-09-add-remove-entity-design.md`). Two separate composed
      calls, not one orchestrated operation, matching the
      rename/add/drop-property split. No duplicate/collision detection on
      either `Add*` method; `RemoveEntity` only handles the bare,
      unchained `Entity<T>(...)` statement shape.
- [x] **`[found]` Remove a fluent config** (e.g. clearing a max length) — the delete counterpart of the insert above.
      **Update:** Implemented as `OnModelCreatingRewriter.RemoveMaxLength`
      alongside "Drop a property" above — see
      `2026-07-08-drop-property-design.md`.

## Priority 2 — Broaden fluent-config coverage (same pattern as `HasMaxLength`)

> `[plan]` explicitly scopes the spike to `HasMaxLength` only; these follow the
> established parse → merge → rewrite pattern.

- [x] **`[spec/plan]` `IsRequired` / nullability** as fluent config (distinct from CLR `?`).
      **Update:** `FluentConfigParser.ParseIsRequired` reads bare `.IsRequired()` and
      explicit `.IsRequired(true/false)` calls into `IsRequiredConfig`;
      `ModelMerger.ApplyIsRequired` folds that into `PropertyModel.IsRequiredOverride`,
      kept separate from CLR-derived `IsNullable`; `OnModelCreatingRewriter.RewriteIsRequired`/
      `RemoveIsRequired` mirror the full `HasMaxLength` rewrite/remove pattern (see
      `2026-07-09-is-required-config-design.md`).
- [x] **`[spec/plan]` Precision / scale** (`HasPrecision`) for decimal.
      **Update:** `FluentConfigParser.ParsePrecisions` reads `HasPrecision(18)`
      and `HasPrecision(18, 2)` into `PrecisionConfig`; `ModelMerger.ApplyPrecisions`
      folds that into `PropertyModel.Precision`/`Scale`;
      `OnModelCreatingRewriter.RewritePrecision`/`RemovePrecision` reuse the
      same four-case dispatch built for `HasMaxLength` (see
      `2026-07-12-has-precision-config-design.md`).
- [x] **`[spec]` Keys** — `HasKey`, including composite keys.
      **Update:** `FluentConfigParser.ParseKeys` reads `HasKey(e => e.Id)`,
      `HasKey(e => new { e.A, e.B })`, `HasKey("Id")`, and
      `HasKey("A", "B")` into `KeyConfig`; `ModelMerger.ApplyKeys` folds
      that into `EntityModel.KeyPropertyNames` (entity-level, not a
      per-property field, since composite key order matters);
      `OnModelCreatingRewriter.SetKey`/`RemoveKey` write it back, always
      emitting the canonical lambda form (see
      `2026-07-09-has-key-config-design.md`).
- [x] **`[spec]` Indexes** — `HasIndex`, including unique.
      **Update:** `FluentConfigParser.ParseIndexes` reads single/composite lambda,
      bare string params, lambda+name, and string-array+name overloads into `IndexConfig`;
      `ModelMerger.ApplyIndexes` folds all matching configs into `EntityModel.Indexes`
      (a list — entities may have multiple indexes); `OnModelCreatingRewriter.SetIndex`/
      `RemoveIndex` write it back using property-set identity (`SequenceEqual`), always
      emitting the canonical lambda form with optional `.IsUnique()` chain and inline
      name arg (see `2026-07-11-has-index-config-design.md`).
- [x] **`[spec]` Relationships** — 1:1, 1:many, many:many (`HasOne`/`WithMany`/`HasForeignKey` etc.).
      **Update:** `FluentConfigParser.ParseRelationships` reads all four shapes
      (`HasOne`/`WithMany`, `HasMany`/`WithOne`, `HasOne`/`WithOne`,
      `HasMany`/`WithMany`), in both the block-nested and bare-`Entity<T>()`-chained
      styles, resolving the related entity via explicit generic type arguments or by
      cross-referencing navigation properties against the already-parsed
      `EntityModel`s; `ModelMerger.ApplyRelationships` maps the results into
      `RelationshipModel` (see
      `2026-07-12-has-relationships-config-design.md`). Parse + merge only — no
      rewriter yet (`SetRelationship`/`RemoveRelationship` deferred to a follow-up
      spec, since there's no diagram consumer yet to validate the write-back shape
      against). `UsingEntity`'s nested join-config, `HasPrincipalKey`, data-annotation
      attributes, and redundant both-sides configuration remain out of scope.
- [x] **`[spec]` Column/table mapping** — `ToTable`, `HasColumnName`, `HasColumnType`, default values.
      **Update:** `FluentConfigParser.ParseTableMappings`/`ParseColumnNames`/
      `ParseColumnTypes`/`ParseDefaultValues` read `ToTable(name[, schema])`,
      `HasColumnName(...)`, `HasColumnType(...)`, and `HasDefaultValue(...)`
      (literals only) into four separate config DTOs;
      `ModelMerger.ApplyTableMapping`/`ApplyColumnNames`/`ApplyColumnTypes`/
      `ApplyDefaultValues` fold them into `EntityModel.TableName`/`Schema`
      and `PropertyModel.ColumnName`/`ColumnType`/`DefaultValueLiteral`;
      `OnModelCreatingRewriter.SetTable`/`SetColumnName`/`SetColumnType`/
      `SetDefaultValue` (and their `Remove*` counterparts) write it back,
      reusing the `HasKey`/`HasMaxLength` dispatch patterns (see
      `2026-07-12-column-table-mapping-config-design.md`). `HasDefaultValueSql`
      and non-literal defaults remain out of scope.
- [x] **`[found]` Diagnostic codes are bare string literals.**
      **Update:** all 12 codes (which had grown well past the original three)
      now live in a `DiagnosticCodes` constants class
      (`src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`); both parsers
      and all test assertions reference the constants instead of bare
      strings.
- [x] **`[found]` `GetPropertyNameFor` doesn't resolve parenthesized lambdas.**
      **Update:** `GetPropertyNameForPropertyCall` (`FluentSyntaxHelpers.cs`) now
      matches `ParenthesizedLambdaExpressionSyntax` (expression-bodied and
      single-`return`-statement block-bodied) the same way it already matched
      `SimpleLambdaExpressionSyntax`, so `entity.Property((Person e) => e.Name)`
      resolves to `"Name"` instead of emitting `UnresolvablePropertyName`.

## Priority 3 — Second config style

- [x] **`[spec/plan]` `IEntityTypeConfiguration<T>` support.** Fast-follow after
      `OnModelCreating`. Structurally easier (one entity per file), but a
      separate parsing/codegen path.
      **Update:** All 10 `FluentConfigParser.Parse*` methods now transparently
      recognize `IEntityTypeConfiguration<T>` classes via a shared
      `FluentSyntaxHelpers.FindConfigurationScopes` helper, alongside the
      existing `Entity<T>()` style — same method signatures, no new public API
      (see `2026-07-13-ientitytypeconfiguration-support-design.md`). Parse +
      merge only; the rewriter (write-back into config classes) is deferred to
      a follow-up spec, matching the precedent set by relationships.

## Priority 4 — The application shell (next plan per `[plan]` "What's next")

- [x] **`[spec/plan]` Blazor WebAssembly shell** referencing `EfSchemaVisualizer.Core`.
      **Update:** Scaffolded, trimmed, wired to `EfSchemaVisualizer.Core`, and
      manually verified in-browser (Roslyn's syntax-tree APIs work under Mono
      WASM; published `_framework` payload 46M) — see
      `2026-07-13-blazor-wasm-shell-design.md` /
      `2026-07-13-blazor-wasm-shell.md`.
- [x] **`[spec]` Read-only ER diagram render** of parsed `EntityModel`s first.
      **Update:** Two-textarea page (entity classes + `OnModelCreating` config)
      runs the full parse/merge pipeline via a new `DiagramModelBuilder` and
      renders entities as property-list nodes with key marking, connected by
      labeled relationship lines, using `Z.Blazor.Diagrams` (MIT-licensed,
      C#-first, chosen with the long-term WYSIWYG drag-and-drop goal in mind)
      — see `2026-07-13-er-diagram-render-design.md` /
      `2026-07-13-er-diagram-render.md`. Fixed-grid layout only, no
      auto-layout. Build/publish and static-asset verification done;
      interactive in-browser verification (drag, live render) was not
      possible in the implementation sandbox (no browser available) and
      remains an open follow-up before the next diagram slice builds on top.
- [x] **`[spec]` Editable diagram** wired to the rewriter (depends on Priority 1).
      Interaction model is WYSIWYG drag-and-drop (drag entities, draw
      relationships), similar to SSMS's table designer — not a form-based
      editor. See the Goal section of `2026-07-07-ef-schema-visualizer-design.md`
      and `2026-07-13-er-diagram-render-design.md`'s library-choice rationale,
      which already picked `Z.Blazor.Diagrams` with this in mind.
      **Update:** Specced as one grand design covering all five phases —
      `2026-07-14-editable-diagram-design.md` — implemented as separate
      phased plans/branches, merged to `main` as each completes.
      - **Phase 1 (rename + type/nullable editing) — done.** Double-click
        rename for entities/properties, inline property type + nullable
        editing, all wired through a new `DiagramEditor`
        (validate → `Core` rewriter call(s) → reparse → rebuild diagram)
        and a new `EntityClassRewriter.ChangePropertyType`. See
        `2026-07-14-editable-diagram-phase1-rename-type.md`. Whole-branch
        review caught and fixed a real bug in the process: renaming an
        entity left dangling navigation-property type references
        elsewhere in the file (silently dropping relationships and
        breaking compilation) — fixed via a new
        `EntityClassRewriter.RenamePropertyTypeReferences`.
      - **Phase 2 (add/remove entities and properties) — done.** Toolbar
        "+ Entity" button, per-node "+ Add property" row, and "×" remove
        buttons on entities/properties, wired to the existing
        `AddClass`/`RemoveClass`/`AddProperty`/`RemoveProperty`/`AddEntity`/
        `RemoveEntity` rewriter methods via new `DiagramEditor` methods.
        Removal is refused (not cascaded) when a key/index/relationship
        still depends on the target, since relationship removal doesn't
        exist yet. See
        `2026-07-14-editable-diagram-phase2-add-remove.md`. Required
        reworking diagram position-preservation from ordinal-index
        matching (only safe while entity count/order couldn't change) to
        stable per-entity `Guid` matching. Whole-branch review caught and
        fixed a crash (hand-editing a new class into a textarea before any
        diagram gesture reparsed the model threw an uncaught
        `KeyNotFoundException`) and a placement bug (new entities spawned
        on top of existing ones instead of grid-placing past them).
      - **Phase 3 (keys and indexes) — done.** Primary-key toggle and
        single-/composite-index add/remove/rename/unique-toggle wired to
        the existing `ToggleKey`/`AddIndex`/`ToggleIndexMembership`/
        `SetIndexUnique`/`RenameIndex`/`RemoveIndex` `DiagramEditor`
        methods, via a new expand-on-click panel per property row on
        `Diagram/EntityNode.razor`.
      - **Phase 4 (column/table mapping, precision, default values) —
        done.** Entity-level table/schema fields in the node header, plus
        column name/type/precision/scale/default-value fields in the
        Phase 3 expand-on-click panel, wired to new `DiagramEditor`
        methods (`SetTableMapping`, `SetColumnName`/`SetColumnType`,
        `SetPrecision`/`SetDefaultValue`). Whole-phase review caught and
        fixed a real bug: `SetPrecision` spuriously rejected clearing
        precision on a property that also had a scale set; fixed to always
        clear the whole `.HasPrecision(...)` mapping when precision is
        blanked.
      - **Phase 5 (relationships) — done.** Built `SetRelationship`/
        `RemoveRelationship` in `Core` first, then `DiagramEditor.
        AddRelationship`/`SetRelationshipShape`/`RemoveRelationship`, then
        wired drag-to-connect ports on entity nodes (always creating a
        default one-to-many relationship) and a click-to-expand
        `RelationshipLinkLabel.razor` panel (Kind dropdown, foreign-key
        dropdown, "Remove relationship" button) in `Pages/Home.razor`.
        This was the last phase of the editable-diagram slice — see
        `2026-07-14-editable-diagram-phase5-relationships.md`.
        Whole-branch review caught and fixed a real bug: `RemoveRelationship`
        only matched the generic-argument (`HasOne<Blog>()`) chain style, so
        removing/reshaping a relationship written in the idiomatic
        navigation-lambda style (`HasOne(e => e.Blog)`, including this app's
        own shipped default sample) silently no-op'd and could produce a
        duplicate relationship on edit; fixed to also match by navigation
        property name and to fail explicitly instead of falsely reporting
        success.
      - **Browser verification — done.** One representative gesture per
        phase was scripted and run against a real Chromium instance,
        confirming each produces correct regenerated source — see
        `2026-07-15-editable-diagram-browser-verification.md`.
- [x] **`[spec]` `.zip` upload / download**, fully client-side, stateless.
      **Update:** `ProjectArchiveReader`/`ProjectArchiveWriter` (new
      `EfSchemaVisualizer.Core/Archive/` classes) classify a zip's `.cs`
      entries into the existing two-blob model (entity classes / EF config)
      and rebuild a zip from it on download, wired into `Home.razor` via a
      new `<InputFile>` and a "Download .zip" button — see
      `2026-07-15-zip-upload-download-design.md`. Deliberately scoped down
      from the original per-file round-trip spec: original file
      names/boundaries and non-`.cs` project files are not preserved; a
      download always produces exactly `Entities.cs` + `DbContext.cs`. Final
      whole-branch review caught and fixed a real bug: the classifier only
      recognized config files wrapped in an `OnModelCreating` method or an
      `IEntityTypeConfiguration` class, so a downloaded zip's bare
      fluent-statement `DbContext.cs` (the app's own `ConfigSource` shape)
      silently failed to reclassify as config on re-upload, dropping all EF
      configuration; fixed by reusing the existing
      `FluentSyntaxHelpers.FindConfigurationScopes` helper instead of ad hoc
      checks.
- [x] **`[spec]` GitHub Actions → GitHub Pages** deploy on push to `main`.
      **Update:** workflow added at `.github/workflows/deploy.yml`. The
      repo's Settings → Pages → Source still needs to be switched to
      "GitHub Actions" manually before the `deploy` job will succeed — not
      something a workflow file can do on its own.
- [x] **`[spec]` Roslyn WASM payload size / first-load time** — measure early, flagged as an open risk.
      **Update:** Re-measured 2026-07-15 against the current full-featured
      app (see `2026-07-13-blazor-wasm-shell-design.md`'s "Re-measurement"
      addendum). Published `_framework` raw size (47.5 MB) is essentially
      unchanged from the original 2026-07-13 spike (46 MB) despite the app
      growing substantially — the fixed Roslyn/Mono-runtime cost dominates,
      app code is a small fraction. Excluding lazy-loaded locale satellite
      resources, a first load needs ~22.6 MB raw / ~6.6 MB with the
      Brotli/gzip compression Blazor's publish step already emits — real
      size depends on whether the eventual GitHub Pages host serves the
      precompressed `.br` variant, unconfirmed until that deploy slice
      exists. No real in-browser first-load *timing* was measured (still no
      browser available in this environment); only a rough transfer-time
      estimate. Not prohibitively large — no re-plan needed — but the
      `wasm-tools` SDK workload (not installed here) could shrink it further
      and real in-browser timing remains a follow-up once possible.

## Priority 5 — Repo hygiene & smaller cleanups

- [x] **`[found]` No README.** `[spec]` makes the README load-bearing for the
      trust story and star/donation prompts — it's the public front door and is
      currently absent.
      **Update:** Added `README.md` covering the pitch, how it works,
      non-goals, project layout, and local dev/test commands.
- [x] **`[spec]` MIT license file.**
      **Update:** Added `LICENSE` (MIT, Michael Bourke, 2026).
- [x] **`[found]` Perf (minor at current scale):** `FluentConfigParser` re-walks
      the whole tree once per distinct entity name (`FluentConfigParser.cs:18-44`,
      O(entities × nodes)); `ModelMerger.ApplyMaxLengths` is O(props × configs)
      via `FirstOrDefault` (`ModelMerger.cs:14`). A single grouped pass / a
      `(entity, property)`-keyed dictionary would fix both.
      **Update:** `FluentSyntaxHelpers.FindConfigurationScopes` now does a
      single `DescendantNodes()` pass, grouping invocations by entity name
      into a dictionary instead of re-walking the tree per distinct name.
      `ModelMerger`'s six per-property `Apply*` methods (`ApplyMaxLengths`,
      `ApplyIsRequired`, `ApplyPrecisions`, `ApplyColumnNames`,
      `ApplyColumnTypes`, `ApplyDefaultValues`) now build a property-keyed
      dictionary once per call via a shared `IndexByProperty` helper instead
      of calling `FirstOrDefault` per property. 303/303 tests still green.
- [x] **`[found]` Namespacing:** `ModelMerger` and `MaxLengthConfig` live under
      `Parsing` but are merge/DTO concerns, not parsing.
      **Update:** `ModelMerger` and all ten `*Config` DTOs it and
      `FluentConfigParser` produce/consume (`MaxLengthConfig`,
      `IsRequiredConfig`, `PrecisionConfig`, `KeyConfig`, `IndexConfig`,
      `TableConfig`, `ColumnNameConfig`, `ColumnTypeConfig`,
      `DefaultValueConfig`, `RelationshipConfig`) moved to a new
      `EfSchemaVisualizer.Core.Merging` namespace/folder, mirrored by moving
      `ModelMergerTests` to a `Merging` test folder. `Parsing` now only
      contains parser/diagnostics/syntax-helper types.
- [x] **`[found]` Null-forgiving noise** (`.Distinct()!`, `entityName!`) in
      `FluentConfigParser` — project to a non-null `List<string>` once instead.
      **Update:** Resolved as a side effect of the perf fix above:
      `FindConfigurationScopes`'s single-pass rewrite groups invocations into
      a dictionary keyed only when `GetConfiguredEntityName` is non-null, so
      the `.Distinct()!`/`entityName!` forgiving operators this item was
      about no longer exist anywhere in the codebase.
- [x] **`[found]` Widen test surface** with the P0 edge cases: empty/class-less
      file, record entity, `Property("Name")` string overload, non-literal
      `HasMaxLength` arg, renamed builder param, multiple classes per file, and
      the not-yet-built add/remove/rename paths.
      **Update:** Already fully covered — `EntityClassParserTests` exercises
      the class-less/record/multi-class-per-file cases,
      `FluentConfigParserTests` exercises the string-overload/non-literal/
      renamed-param cases, and `EntityClassRewriterTests`/
      `OnModelCreatingRewriterTests` carry 85+ tests across the add/remove/
      rename paths. No gaps found; 303/303 tests green.
- [x] **`[found]` No test project for `EfSchemaVisualizer.Web`.** Only
      `EfSchemaVisualizer.Core` has automated test coverage; the Web project
      (Razor components, `DiagramEditor`, `DiagramSync`) has none. Surfaced by
      the 2026-07-15 browser-verification pass
      (`2026-07-15-editable-diagram-browser-verification.md`): two of the
      three real bugs it found and fixed — `EntityNode.razor` missing
      `aria-label`s, and `DiagramSync.Rebuild`'s node-identity/reuse logic
      (the fix that stops an edit from wiping an expanded property panel's
      state) — have no regression coverage as a result, guarded only by an
      ad hoc, uncommitted Playwright script that isn't re-run in CI.
      `DiagramSync.Rebuild` in particular is pure and DI-free, so it's
      readily unit-testable against a `BlazorDiagram` once a
      `EfSchemaVisualizer.Web.Tests` project exists.
      **Update:** Added `tests/EfSchemaVisualizer.Web.Tests` (referenced from
      `EfSchemaVisualizer.slnx`). `DiagramSyncTests` covers `Rebuild`'s
      node-identity/reuse logic directly against a real `BlazorDiagram`
      (new/removed/kept entities, node-instance reuse across edits, link
      clear-and-recreate, unresolvable-relationship skip, grid placement).
      The `aria-label` regression turned out not to be coverable via full
      component rendering: `EntityNode`'s `PortRenderer` children throw a
      `NullReferenceException` in `OnAfterRenderAsync` under bUnit because
      they depend on real browser layout APIs
      (`getBoundingClientRect`/`ResizeObserver`) bUnit's headless render
      tree doesn't provide — so `EntityNodeAccessibilityTests` instead
      asserts directly against `EntityNode.razor`'s markup source that every
      button with a `title` has a matching `aria-label`; verified it fails
      when an `aria-label` is stripped. 311/311 tests green across both
      projects.

---

# Round 2 review — 2026-07-16

> Everything above is `[x]` complete (311/311 tests green at `6a98131`). This
> section is a fresh pass over the built code looking for gaps the original
> backlog didn't anticipate. None of the below is started. Ordered by how
> directly it undercuts the tool's core "read your real project and trust the
> output" promise.

## Priority 0 — Silent data loss on real-world input

- [x] **`[found]` Data-annotation configuration is completely unread.**
      `EntityClassParser` only inspects attributes to *exclude* `[NotMapped]`
      properties (`EntityClassParser.cs:86,102`). `[Key]`, `[Required]`,
      `[MaxLength]`/`[StringLength]`, `[Column]`, `[Table]`, `[Precision]`,
      `[ForeignKey]`, `[DatabaseGenerated]` etc. are all ignored. A huge share
      of real EF projects configure the model this way instead of (or alongside)
      the fluent API, so those models render with missing keys, missing
      constraints, and wrong nullability — with **no diagnostic**. This is the
      biggest single "your real project renders wrong" gap. At minimum emit a
      diagnostic when annotation attributes are present; ideally parse them into
      the same model the fluent path feeds.
      **Update:** `EntityClassParser` now parses `[Required]`, `[MaxLength]`/
      `[StringLength]`, `[Column]`, `[Table]`, `[Precision]`, `[Key]`, and
      `[ForeignKey]` directly into `EntityModel`/`PropertyModel`/
      `RelationshipConfig`, and `DiagramModelBuilder.Build` unions
      annotation-derived relationships with fluent-derived ones (fluent wins
      on conflict, keyed by `(PrincipalEntity, DependentEntity,
      ForeignKeyProperties)`) — see
      `docs/superpowers/plans/2026-07-16-annotation-parsing-and-p0-diagnostics.md`.
      Scalar fields (max length, required, column, precision, table, key)
      get fluent-wins precedence for free via `ModelMerger.Apply*`, which
      only overwrites a field when a matching fluent config exists. 338/338
      tests green.

- [x] **`[found]` Duplicate entity (class) names collide silently.**
      `DiagramEditor` keys entities by bare `Name` in a
      `Dictionary<string, Guid>` (`DiagramEditor.cs:26,36`), and
      `DiagramSync.Rebuild` indexes `entityIds[entity.Name]`
      (`DiagramSync.cs:34`). Two classes with the same short name (same name in
      two namespaces, a partial class split unusually, or an entity plus a
      same-named type) overwrite each other in the id map and merge/render as
      one. Detect duplicate names during parse and surface a diagnostic rather
      than dropping one.
      **Update:** `EntityClassParser.Parse` now detects duplicate class names
      and emits a diagnostic instead of silently colliding — see
      `docs/superpowers/plans/2026-07-16-annotation-parsing-and-p0-diagnostics.md`.
      338/338 tests green.

- [x] **`[found]` Nested type declarations are dropped without a diagnostic.**
      `EntityClassParser.Parse` filters to top-level types
      (`!t.Ancestors().OfType<TypeDeclarationSyntax>().Any()`,
      `EntityClassParser.cs:20`). An entity declared as a nested class is
      silently invisible. Namespaces are fine; nested types are not. Emit a
      diagnostic at least.
      **Update:** `EntityClassParser.Parse` now emits a diagnostic when a
      nested type declaration is skipped — see
      `docs/superpowers/plans/2026-07-16-annotation-parsing-and-p0-diagnostics.md`.
      338/338 tests green.

## Priority 1 — Read/write capability mismatches (renders, can't save back)

- [x] **`[found]` `IEntityTypeConfiguration<T>` is parse-only — edits can't be
      written back.** Per Priority 3 above, the parser reads config classes but
      the rewriter (`OnModelCreatingRewriter`) only writes into
      `modelBuilder.Entity<T>(...)` blocks. So a project whose config lives in
      `IEntityTypeConfiguration` classes (which the README advertises as
      supported) renders correctly but every diagram edit silently no-ops or
      targets the wrong place. Either build the config-class rewriter path or
      disable/flag editing when the source uses that style.
      **Update:** Every `OnModelCreatingRewriter` mutator now resolves its
      target scope via the existing `FluentSyntaxHelpers.FindConfigurationScopes`
      (previously used only by the parser), so edits land in whichever scope
      an entity's config already lives in — `Entity<T>()` block or
      `IEntityTypeConfiguration<T>.Configure` method — via a shared
      `GetScopeBlockAndReceiver` helper. Rename now also patches the config
      class's base-list generic argument and `Configure` parameter type;
      remove now deletes the whole config class. New entities still always
      synthesize into `OnModelCreating`, unchanged, and an entity configured
      in both styles simultaneously has edits prefer the `Entity<T>()` block
      (see `2026-07-16-ientitytypeconfiguration-rewriter-design.md`).

- [x] **`[found]` Record positional parameters render but aren't editable.**
      `EntityClassParser.ParseParameterProperty` reads record primary-constructor
      params as properties, but the rewriter explicitly doesn't touch positional
      params (documented in the add/drop/rename items above). Result: rename /
      change-type / remove on a positional property silently fails or no-ops.
      Support them in the rewriter, or mark those rows read-only in the UI so the
      failure isn't silent.
      **Update:** `EntityClassRewriter.RenameProperty`/`ChangePropertyType`/
      `RemoveProperty` now fall back to searching the type's primary-constructor
      `ParameterList` whenever no matching `PropertyDeclarationSyntax` member is
      found, operating on the matching `ParameterSyntax` instead (rename via
      `WithIdentifier`, retype via `WithType`, remove via rebuilding the
      `ParameterList` with the parameter removed). Investigation found the
      backlog's "silently fails or no-ops" description was inaccurate — the
      actual prior behavior was an unhandled `InvalidOperationException` thrown
      from the `?? throw` on the property lookup, since `DiagramEditor`/
      `EntityNode.razor` have no try/catch around these calls; this was a crash,
      not a silent no-op. Also fixed the related dangling-reference gap in
      `RenamePropertyTypeReferences` (used when renaming an entity, to fix up
      navigation-property type references elsewhere): it only searched
      `PropertyDeclarationSyntax` types, so a navigation property declared as a
      positional parameter (e.g. `record Order(Customer Customer)`) kept
      pointing at the old type name after an entity rename; now it also scans
      every type declaration's `ParameterList`. 7 new tests added to
      `EntityClassRewriterTests`; 373/373 tests green across all three test
      projects.

## Priority 2 — App shell robustness & UX

- [x] **`[found]` Raw exception dumps are shown to the user.** `Home.razor`
      sets `_error = ex.ToString()` (full stack trace) on both the render path
      (`Home.razor:134`) and the zip-upload path (`Home.razor:211`), rendered in
      a red `<pre>`. Show a friendly message; log the detail to the browser
      console instead.
      **Update:** Both catch blocks now set a short, path-specific friendly
      `_error` message and call a new `LogErrorAsync` helper
      (`await JS.InvokeVoidAsync("console.error", ex.ToString())`) with the
      full exception detail. `RenderDiagram` became `RenderDiagramAsync` (it
      needs to `await` the JS interop call), and `OnZipSelected` awaits it
      instead of calling it synchronously. Browser-verified with a headless
      Chromium run against the real published app: uploading a corrupt (non-
      zip) file now shows "Something went wrong while reading the uploaded
      .zip file. See the browser console for details." in the red `<pre>`
      (no stack trace), while the full `System.IO.InvalidDataException` with
      stack trace lands in `console.error`. Confirmed the render path (fed
      via pasted source) is largely unreachable for ordinary malformed
      input — the parsing pipeline already turns bad C#/config into
      diagnostics rather than exceptions (per the Priority 0 hardening
      above), so this catch is now a defensive backstop rather than a
      commonly-hit path; both catch blocks share the same code shape and
      `LogErrorAsync` helper, so the fix applies uniformly regardless.

- [x] **`[found]` Textarea `<label>`s aren't associated with their inputs.**
      `Home.razor:23,27` use bare `<label>Entity classes</label>` with no
      `for`/`id` linkage, so screen readers don't announce them. (The in-diagram
      controls already got `aria-label`s in the last round — this is the one
      spot that was missed.)
      **Update:** Both `<label>`s in `Home.razor` now carry `for="class-source"`
      / `for="config-source"` matching the adjacent `<textarea id="...">`.

- [x] **`[found]` No undo/redo.** For a visual editor every gesture rewrites the
      source irreversibly; the only "undo" is Ctrl-Z inside a textarea, which
      then desyncs from the diagram. A source-snapshot undo stack in
      `DiagramEditor` would be cheap (it already funnels every mutation through
      `Apply`).
      **Update:** `DiagramEditor` now maintains `_undoStack`/`_redoStack` of
      `(ClassSource, ConfigSource, EntityIds)` snapshots; `Apply` (the single
      funnel every gesture-driven mutation already goes through) pushes the
      pre-mutation snapshot and clears the redo stack, so no-op edits that
      return `Ok()` without calling `Apply` correctly push nothing. New public
      `CanUndo`/`CanRedo`/`Undo()`/`Redo()` restore a snapshot (including
      `_entityIds`, so undone/redone diagram nodes keep stable identity) and
      rebuild `Current` via `DiagramModelBuilder.Build`. Hand-edits via
      `SyncSource` (raw textarea typing) deliberately do not push undo state,
      matching the item's framing that undo tracks diagram *gestures*, not
      every keystroke. Wired to new Undo/Redo buttons in `Home.razor`'s
      toolbar and called out in the "How to edit the diagram" legend. 7 new
      tests in `DiagramEditorTests.cs`; 386/386 tests green across all three
      test projects.

- [x] **`[found]` Diagram gestures are undiscoverable.**
      Nothing on the page explained the interaction model (double-click a
      name/type to edit, drag between ports to draw a relationship, click a
      property to expand options). A short legend or first-run hint would help;
      at least document the gestures in the README.
      **Update (2fba175):** the double-click *rename/edit* affordance is now
      obvious — the entity title, property name, and property type carry a
      dashed underline, text cursor, tooltips, and a ✎ pencil on the title, the
      node hint line leads with "Double-click a name or type to edit", and the
      "Table:" field is tooltipped to clarify it sets `.ToTable` rather than the
      class name (the exact mix-up that surfaced this).
      **Update:** Closed the remaining gap. The per-node hint line now also
      calls out the property-expand gesture ("Click ▸ next to a property for
      more options"). Added a collapsible "How to edit the diagram" `<details>`
      legend above the canvas in `Home.razor`, shown whenever a diagram is
      rendered, listing all four gesture families (rename/retype, drag-to-
      connect, property-expand panel, nullable checkbox/remove buttons).
      README's "How it works" step 3 now spells out the double-click and
      expand-panel gestures explicitly and points at the in-app legend.

## Priority 3 — Deploy & CI hardening

- [x] **`[found]` GitHub Pages base-href rewrite is a brittle `sed`.**
      `deploy.yml:36` string-replaces the exact literal `<base href="/" />`. If
      the Blazor template ever emits that tag differently (spacing, self-close,
      an added attribute), the `sed` becomes a silent no-op and **every asset
      404s on Pages** with a green build. Prefer
      `dotnet publish -p:StaticWebAssetBasePath=WasmEFVisualDesigner` (the
      csproj already sets `OverrideHtmlAssetPlaceholders`), or fail the step if
      the replacement count is zero.
      **Update:** Tried `-p:StaticWebAssetBasePath` first — it moves published
      assets under a `wwwroot/<subpath>/` folder but leaves `index.html` (and
      its relative `_framework` references) at the root, so it doesn't
      actually fix anything for a GitHub Pages project-site layout, where the
      *server* prefixes the whole `wwwroot` tree with `/reponame/` rather than
      the files being physically nested. Went with the documented fallback
      instead: the patch step now counts matches with a spacing/self-close
      tolerant regex before and after the substitution and fails the workflow
      (`::error::`) if either count is zero, so a template change trips CI
      instead of silently shipping 404s.

- [x] **`[found]` No warnings-as-errors / analyzer gate.** Neither csproj sets
      `TreatWarningsAsErrors`, and CI has no `dotnet format --verify-no-changes`
      step, so warnings and style drift accumulate invisibly.
      **Update:** Added `Directory.Build.props` at the repo root setting
      `TreatWarningsAsErrors`, `EnableNETAnalyzers`, and
      `AnalysisLevel=latest` for every project; added a `Format check` step to
      `deploy.yml` running `dotnet format EfSchemaVisualizer.slnx
      --verify-no-changes` before `Test`. Solution was already warning- and
      format-clean, so no code changes were needed to turn these on.

- [x] **`[found]` No CI smoke test that the published WASM app actually boots.**
      The one-off browser verification was an uncommitted Playwright script
      (noted above) that never runs in CI, and `Home.razor`'s `@code` (zip
      upload, relationship drag wiring, error handling, `OnDiagramEditedAsync`)
      has zero automated coverage. A headless-Chromium smoke test in the deploy
      workflow, plus extracting `Home.razor` logic into a testable class, would
      close both.
      **Update:** Added `tests/EfSchemaVisualizer.SmokeTests` (Microsoft.Playwright
      .NET, no Node/`package.json`), whose one test serves a real `dotnet
      publish` output over a local Kestrel static-file host and drives headless
      Chromium against it, asserting the app's source textareas render and no
      console/network errors fired. Self-skips (no-ops to a pass) when the
      `SMOKE_TEST_PUBLISH_DIR` env var isn't set, so it's inert in the existing
      `dotnet test EfSchemaVisualizer.slnx` step; `deploy.yml` runs it for real
      as a dedicated step right after `Publish` (before the base-href patch, so
      it exercises the unmodified `href="/"` output), installing Chromium via
      the generated `playwright.ps1`. Getting it green surfaced two real gaps
      in a bare `UseStaticFiles()` host that a real static host (GitHub Pages,
      `dotnet run`) papers over: it doesn't serve `index.html` for `/` without
      `UseDefaultFiles()`, and it 404s WASM's unrecognized extensions
      (`.dat`, etc.) without `ServeUnknownFileTypes = true` — both are now
      commented in `AppBootSmokeTests.cs`. Extracting `Home.razor`'s `@code`
      into a testable class remains out of scope; this closes the "app boots"
      half of the item, not full `@code` coverage.

- [x] **`[found]` No round-trip / idempotency fuzz test.** The trust story rests
      on parse→edit→regenerate not losing data, but there's no test that feeds a
      corpus of realistic DbContext files through the pipeline and asserts the
      unsupported constructs are *preserved verbatim* (not dropped) and that a
      no-op edit is byte-stable. Add one over a small corpus of real-world
      shapes.
      **Update:** Added `RoundTripFuzzTests.cs` (Core.Tests) against a
      two-entity, multi-config-kind corpus (keys, table mapping, max length,
      required, column name/type, index, relationships, plus a
      `HasDefaultValueSql` call the parser doesn't model at all). Confirms:
      the unsupported `HasDefaultValueSql` construct is dropped from the
      model (documented gap, not silently corrupted); every config kind's
      no-op write-back round-trips (byte-identical for the pure-mutation
      paths — `HasMaxLength`, `IsRequired`, column name/type, default value —
      and content-identical modulo the already-documented whole-file
      `NormalizeWhitespace()` line-ending/blank-line normalization for the
      synthesis paths — `HasKey`, `ToTable`, `HasIndex`); and renaming one
      property leaves every other entity's config, including the
      `HasDefaultValueSql` line, untouched.

## Priority 4 — Documentation

- [x] **`[found]` README project-layout section is stale.** It lists only
      `tests/EfSchemaVisualizer.Core.Tests` (README:52) but
      `tests/EfSchemaVisualizer.Web.Tests` now also exists. Update it.
      **Update:** Added the missing `tests/EfSchemaVisualizer.Web.Tests`
      bullet to the README's project-layout list.

- [x] **`[found]` No single "what EF features are unsupported" list.** Known
      gaps are scattered across specs: `HasDefaultValueSql`, `HasPrincipalKey`,
      `UsingEntity` join config, owned/complex types, value converters,
      inheritance (TPH/TPT), enums, non-literal argument values. Collect them in
      one README/docs section so users know before they upload what will be
      dropped (and whether a diagnostic fires for each).
      **Update:** Added an "Unsupported EF Core features" README section
      listing all of the above, noting that none of them raise a diagnostic
      today, and pointing at `DiagnosticCodes.cs` for the current, authoritative
      list of what *does* get flagged (the non-literal-argument cases).

---

# Round 3 review — 2026-07-16

> Everything above is `[x]` complete (386/386 tests green at `9d76cf0`). This
> section is a fresh audit of the app against EF Core's real surface area:
> capability that exists in `Core` but has no UI, EF features that are
> silently dropped, and app-level features that are missing. Ordered by how
> directly each undercuts the "read your real project and trust the output"
> promise. None of the below is started.

## Priority 0 — Data loss on the edit path

- [x] **`[found]` Composite foreign keys are truncated to one property by the
      relationship panel.** The model carries `ForeignKeyProperties` as a list
      and `FluentConfigParser` reads composite FKs correctly, but
      `RelationshipLinkLabel.razor` renders a single-select `<select>` and
      commits `new[] { _foreignKeyProperty }` (`RelationshipLinkLabel.razor:93-95`).
      Any edit through the panel — even just toggling Kind — silently rewrites
      a composite FK down to its first property. This is data loss on edit,
      not a missing feature. Fix: multi-select (checkbox list, like the index
      panel's membership toggles) or at minimum refuse to commit when the
      existing relationship has a composite FK.
      **Update:** Replaced the single-select `<select>` with a checkbox list
      (one row per dependent-entity property), mirroring the existing
      index-membership checkbox pattern in `EntityNode.razor`. `_foreignKeyProperty`
      (single `string`) became `_foreignKeyProperties` (ordered `List<string>`),
      seeded from `Label.Relationship.ForeignKeyProperties` on expand; a new
      `ToggleForeignKeyProperty` handler appends on check / removes on uncheck,
      preserving order (significant for composite FK-to-key correspondence and
      for `DiagramEditor.SetRelationshipShape`'s `SequenceEqual` no-op check —
      no `DiagramEditor`/`SetRelationshipShape` changes were needed, it already
      took an ordered `IReadOnlyList<string>`). 386/386 tests green.

## Priority 1 — The unrecognized-call diagnostic (highest-leverage trust fix)

- [x] **`[found]` Emit a diagnostic for any fluent call the parser doesn't
      recognize.** The README's "Unsupported EF Core features" section admits
      that everything unread is dropped with *no signal*. Rather than parsing
      every remaining EF feature, add one generic diagnostic:
      `FluentSyntaxHelpers.FindConfigurationScopes` already isolates every
      invocation inside each entity's config scope, so a single pass can flag
      any chained call whose method name isn't in the known set
      (`HasMaxLength`, `HasKey`, `IsRequired`, …) with an
      `UnrecognizedConfigCall` diagnostic naming the call and entity. One
      change turns every silently-dropped feature in Priority 3 below into a
      warned-about feature, and the README's "no diagnostic fires" caveat
      largely disappears. Care needed to not flag chain-links that are part
      of recognized patterns (e.g. `WithMany` inside a parsed relationship
      chain, `IsUnique` after `HasIndex`).
      **Update:** Added `FluentConfigParser.ParseUnrecognizedCalls`, wired
      into `DiagramModelBuilder.Build`. A new `FluentSyntaxHelpers.
      FindConfigChainCalls` walks only the fluent *chain spine* of each
      config statement (following `.Expression.Expression` down through
      `MemberAccessExpressionSyntax`/`InvocationExpressionSyntax` links, plus
      any call chained directly onto a bare `Entity<T>()` scope with no
      lambda block), rather than a blind descendant walk — deliberately not
      descending into argument expressions (lambda bodies, helper-method
      calls used as arguments), so e.g. `HasMaxLength(GetMaxLength())`
      doesn't misattribute the argument's own invocation as an unrecognized
      chain link. Any call name not in a `RecognizedCallNames` set (the union
      of every name read by the existing `Parse*` methods) is flagged with
      `DiagnosticCodes.UnrecognizedConfigCall`. Respects the same nested-
      `Entity<T>()` opaque boundary as `FindCallsNamed` (which itself was
      refactored to share the underlying walk via a new `FindAllCalls`
      helper, no behavior change). `FluentConfigParser`'s private
      `WalkRelationshipTailChain` was promoted to a shared
      `FluentSyntaxHelpers.WalkChainedTail` and reused by both the
      relationship parser and the new chain-call finder. 9 new tests
      (unrecognized call, chained-after-recognized-call, known chains
      including `IsUnique`/`WithMany` not flagged, helper-method-as-argument
      not flagged, nested-entity attribution, bare-chained tail call,
      bare-chained relationship not flagged, `IEntityTypeConfiguration`
      style). 394/394 tests green across all three test projects.

## Priority 2 — Parsed and rewritable in Core, but no UI

> These are fully round-trippable today — the diagram just never shows them.
> Each is a small addition to `EntityNode.razor` / `RelationshipLinkLabel.razor`
> plus a thin `DiagramEditor` method over existing rewriter calls.

- [x] **`[found]` Max length isn't editable in the app.** The project's
      original flagship feature: `PropertyModel.MaxLength` is parsed and
      `OnModelCreatingRewriter.RewriteMaxLength`/`RemoveMaxLength` exist, but
      the property expand panel has no "Max length" field. Add one alongside
      Column name/type.
      **Update:** Added a "Max length" number field to `EntityNode.razor`'s
      property expand panel, wired to a new `DiagramEditor.SetMaxLength`
      (mirrors the existing `SetColumnName`/`SetPrecision` shape: clears via
      `RemoveMaxLength` when blanked, rejects non-positive values, calls
      `RewriteMaxLength` otherwise).
- [x] **`[found]` `IsRequired` override isn't editable in the app.**
      `PropertyModel.IsRequiredOverride` is parsed and
      `RewriteIsRequired`/`RemoveIsRequired` exist, but the UI's "nullable"
      checkbox only changes the CLR `?` — a distinct concept in EF. Add a
      tri-state control (no override / required / not required) to the expand
      panel.
      **Update:** Added a "Required override" `<select>` (no override /
      Required / Not required) to the property expand panel, wired to a new
      `DiagramEditor.SetRequiredOverride` (same shape as `SetMaxLength`,
      calling `RemoveIsRequired`/`RewriteIsRequired`).
- [x] **`[found]` Delete behavior isn't editable in the app.**
      `OnDelete(DeleteBehavior.X)` is parsed into
      `RelationshipModel.OnDeleteBehavior` and `SetRelationship` writes it
      back (`OnModelCreatingRewriter.cs:1098`), but the relationship panel
      only offers Kind/FK/Remove. Add a Cascade/Restrict/SetNull/NoAction
      dropdown.
      **Update:** Added an "On delete" `<select>` (default/Cascade/Restrict/
      SetNull/NoAction) to `RelationshipLinkLabel.razor`'s expand panel.
      `DiagramEditor.SetRelationshipShape` gained a fourth
      `newOnDeleteBehavior` parameter (its no-op/equality check now also
      compares `OnDeleteBehavior`), committed together with Kind/FK on every
      change, matching the panel's existing "commit the whole shape at once"
      pattern. Hidden for many-to-many relationships, since EF has no FK to
      cascade there.
- [x] **`[found]` Many-to-many join entity name isn't shown.**
      `RelationshipModel.JoinEntityName` is parsed and written back via
      `UsingEntity`, but the relationship panel never displays it. Show it
      (read-only is fine as a first step) when Kind is many-to-many.
      **Update:** `RelationshipLinkLabel.razor` now shows a read-only
      "Join entity: {name}" line in place of the FK/delete-behavior fields
      when `Kind` is many-to-many and a `JoinEntityName` is present.

      All four verified end-to-end against a real `dotnet publish` build in
      headless Chromium (not just unit tests): setting max length + required
      override on a property produced
      `entity.Property(e => e.Title).IsRequired().HasMaxLength(200);` in the
      regenerated config source; setting delete behavior on a one-to-many
      relationship produced `.OnDelete(DeleteBehavior.Cascade)`; a
      many-to-many relationship configured via `UsingEntity<StudentCourse>()`
      showed "Join entity: StudentCourse" in the panel. 7 new
      `DiagramEditorPropertyPanelTests` cover `SetMaxLength`/
      `SetRequiredOverride`/`SetRelationshipShape`'s new parameter directly.
      401/401 tests green across all three test projects.

## Priority 3 — EF features not parsed at all (silently dropped)

> Beyond the README's existing unsupported list (owned/complex types,
> `HasConversion`, TPH/TPT/TPC, `HasDefaultValueSql`, `HasPrincipalKey`,
> `UsingEntity` internals, enums). All of the below are also unread today,
> most are common in real projects, and none fire a diagnostic (the Priority 1
> item above is the cheap mitigation for all of them; parsing is the real fix,
> roughly in this order of real-world frequency):

- [x] **`[found]` `Ignore` is unread — ignored members render as mapped.**
      `entity.Ignore(e => e.X)` and `modelBuilder.Ignore<T>()` are dropped, so
      properties/entities the user explicitly excluded from the model show up
      in the diagram as real columns/tables. Arguably a P0-class wrongness;
      listed here because the fix is a parser addition.
      **Update:** `FluentConfigParser.ParseIgnoredProperties` reads
      property-level `entity.Ignore(e => e.X)`/`entity.Ignore("X")` into a new
      `IgnoreConfig`, folded in via `ModelMerger.ApplyIgnoredProperties`;
      `FluentConfigParser.ParseIgnoredEntities` reads whole-entity
      `modelBuilder.Ignore<T>()` (disambiguated from the property-level form
      purely by call shape — generic+zero-args vs non-generic+one-arg) and
      `DiagramModelBuilder.Build` drops the matching entity and any
      relationship referencing it as principal or dependent — see
      `2026-07-17-ignore-index-valuegen-shadow-props-design.md`. Parse +
      merge only, no rewriter/write-back.
- [x] **`[found]` `[Index]` class-level attribute unread.** Very common since
      EF Core 5 (scaffold emits it). Should fold into `EntityModel.Indexes`
      exactly like fluent `HasIndex`, fluent-wins on conflict.
      **Update:** `EntityClassParser.ParseIndexAttributes` reads `[Index]`
      (including `AllowMultiple`, `nameof()`/string-literal property args, and
      the `IsUnique`/`Name` named args) into the existing `IndexConfig` DTO;
      `DiagramModelBuilder.Build` merges attribute- and fluent-derived
      `IndexConfig`s with fluent-wins-on-conflict (same-property-set
      collisions drop the attribute entry, disjoint sets keep both).
- [x] **`[found]` Value generation unread.** `ValueGeneratedOnAdd`/`OnUpdate`/
      `Never`, `UseIdentityColumn`. Identity columns are near-universal;
      at minimum surface as a property badge.
      **Update:** `FluentConfigParser.ParseValueGeneration` reads all five
      calls into a new `PropertyModel.ValueGenerated` (`string?`, matching the
      codebase's existing string-vocabulary precedent for display-only EF
      fields) via a new `ValueGenerationConfig`/`ModelMerger.ApplyValueGeneration`;
      `EntityNode.razor` shows it as a small read-only badge next to the
      property's type. No editor.
- [x] **`[found]` Shadow properties dropped.** `entity.Property<string>("CreatedBy")`
      configures a property with no CLR member; merge finds no match and the
      config vanishes. Model them as properties flagged `IsShadow` (read-only
      rows in the UI until the rewriter learns to create them).
      **Update:** `FluentConfigParser.ParseShadowProperties` reads generic
      `Property<T>("Name")` calls into a new `ShadowPropertyConfig`;
      `ModelMerger.ApplyShadowProperties` synthesizes a `PropertyModel` with
      `IsShadow: true` for any config whose name doesn't already match a real
      property (a same-named real property wins, no duplicate); `EntityNode.razor`
      renders shadow rows as a dimmed, read-only single line with no
      rename/retype/remove/expand-panel affordances. Whole-branch review
      flagged one minor cross-task gap: value generation configured on a
      shadow property never shows a badge, since `ApplyValueGeneration` runs
      before `ApplyShadowProperties` synthesizes the row — an obscure
      combination, display-only, not fixed in this slice.
- [x] **`[found]` Keyless/view entities unread.** `HasNoKey()`, `ToView(...)`,
      `ToSqlQuery(...)`, `[Keyless]`. A scaffolded database-first project with
      views renders these as ordinary tables missing a key.
      **Update:** `FluentConfigParser.ParseViewMappings`/`ParseSqlQueries`/
      `ParseKeylessEntities` and `EntityClassParser`'s `[Keyless]` attribute
      handling feed `EntityModel.ViewName`/`Schema`/`SqlQuery`/`IsKeyless`;
      `OnModelCreatingRewriter.SetView`/`RemoveView`/`SetSqlQuery`/
      `RemoveSqlQuery`/`SetKeyless`/`RemoveKeyless` write it back, with
      `SetKeyless`/`SetKey` enforcing `HasNoKey`/`HasKey` mutual exclusion
      (a hard EF invariant) — see
      `2026-07-20-keyless-view-entities-design.md`. `ToTable`/`ToView` are
      deliberately not cross-cleared. `DiagramEditor.SetViewMapping`/
      `SetSqlQuery`/`SetKeyless` and new `EntityNode.razor` header fields
      (View, SQL query, Keyless checkbox, which also disables the
      per-property primary-key toggle) complete the edit path.
- [x] **`[found]` Concurrency tokens unread.** `IsRowVersion()`,
      `IsConcurrencyToken()`, `[Timestamp]`, `[ConcurrencyCheck]`.
      **Update:** `FluentConfigParser.ParseConcurrencyTokens` reads both fluent
      calls into a new `ConcurrencyTokenConfig` (two independent bools, since
      the calls can co-occur on one property); `EntityClassParser.ParseProperty`
      reads `[Timestamp]`/`[ConcurrencyCheck]` the same way `[Required]` is
      read; `ModelMerger.ApplyConcurrencyTokens` ORs attribute- and
      fluent-derived flags together (fluent only ever raises, never lowers).
      Unlike the previous `ValueGenerated` badge-only pass, this one includes
      full write-back: `OnModelCreatingRewriter.SetRowVersion`/
      `RemoveRowVersion`/`SetConcurrencyToken`/`RemoveConcurrencyToken` (two
      independent bare-marker-call Set/Remove pairs sharing private
      `SetBareMarkerCall`/`RemoveBareMarkerCall` helpers), `DiagramEditor.
      SetRowVersion`/`SetConcurrencyToken`, and two checkboxes ("Row version",
      "Concurrency token") in `EntityNode.razor`'s property expand panel — see
      `2026-07-21-concurrency-tokens-design.md`.
- [x] **`[found]` Alternate keys unread.** `HasAlternateKey(...)` — also a
      valid `HasForeignKey` principal target, so its absence can make parsed
      relationships subtly wrong.
      **Update:** `FluentConfigParser.ParseAlternateKeys` reads
      `HasAlternateKey(...)` calls (lambda, composite, and string-param forms)
      via the shared `FluentSyntaxHelpers.TryReadPropertyNameList` helper
      into a new `AlternateKeyConfig` (property-set name list); unparseable
      arguments are flagged with `DiagnosticCodes.
      UnreadableHasAlternateKeyArgument`. `EntityModel.AlternateKeys` stores
      these as a list of lists (since an entity can have multiple alternate
      keys); `ModelMerger.ApplyAlternateKeys` performs full-replace merge per
      entity (matching by property-set identity). Both `ParseAlternateKeys`
      and `ApplyAlternateKeys` are wired into `DiagramModelBuilder.Build`.
      `"HasAlternateKey"` was added to `FluentConfigParser.
      RecognizedCallNames` to avoid flagging it as an unrecognized config
      call. Write-back: `OnModelCreatingRewriter.AddAlternateKey`/
      `RemoveAlternateKey` (both no-op-safe, reusing `BuildHasKeyArgumentList`
      for argument generation), `DiagramEditor.AddAlternateKey`/
      `ToggleAlternateKeyMembership`/`RemoveAlternateKey` (mirroring the
      `HasIndex` pattern), and a new "Alternate keys:" section in
      `EntityNode.razor`'s property expand panel with membership checkboxes,
      remove button, and "+ New alternate key" button (no name/unique fields,
      since `HasAlternateKey` omits both). `HasPrincipalKey` and relationship
      cross-referencing remain explicitly out of scope — see
      `2026-07-21-alternate-keys-design.md` — and `HasPrincipalKey` remains
      listed in the README's unsupported EF Core features section. 549/549
      tests green across all three test projects.
- [x] **`[found]` Index extras unread.** `HasFilter(...)`, `IsDescending(...)`,
      `IncludeProperties(...)` chained after a parsed `HasIndex` are dropped
      on index rewrite (the rewriter re-emits the canonical chain without
      them) — meaning an index *edit* can silently strip them. The parse gap
      doubles as an edit-path data-loss risk.
      **Update:** `FluentConfigParser.ParseIndexes` now walks the whole
      `HasIndex(...)` chain tail (any order, since EF allows
      `IsUnique`/`HasFilter`/`IsDescending`/`IncludeProperties` in any
      sequence) via a new `ReadIndexExtras` helper built on the existing
      `FluentSyntaxHelpers.WalkChainedTail`, folding results into three new
      `IndexConfig`/`IndexModel` fields (`Filter`, `IsDescending`,
      `IncludeProperties`); unreadable arguments get their own diagnostics
      (`UnreadableHasFilterArgument`/`UnreadableIsDescendingArgument`/
      `UnreadableIncludePropertiesArgument`) and the three call names were
      added to `RecognizedCallNames` so they stop tripping
      `UnrecognizedConfigCall`. `OnModelCreatingRewriter.SetIndex` gained
      matching optional parameters and now re-emits all three on every
      mutate/insert path; `DiagramEditor.ToggleIndexMembership`/
      `SetIndexUnique`/`RenameIndex` now pass the current model's
      `Filter`/`IsDescending`/`IncludeProperties` through on every edit,
      closing the actual data-loss bug (previously, toggling unique or
      renaming an index via the diagram silently dropped these three).
      No UI to edit these three directly yet (parse/merge/rewrite-preserve
      only, matching the precedent set by relationships/value-generation).
      565/565 tests green across all three test projects.
- [x] **`[found]` Data seeding (`HasData`) unread.** Harmless to the diagram,
      but an entity remove/rename leaves orphaned seed data behind; at minimum
      warn.
      **Update:** Investigation found the "no diagnostic fires" half of this
      item was already resolved as a side effect of the Priority 1
      `UnrecognizedConfigCall` diagnostic — since `HasData` was never added to
      `RecognizedCallNames`, every `HasData(...)` call already gets flagged
      generically today, and that diagnostic recomputes (and stays visible)
      after every diagram edit including removes. The real remaining gap was
      a genuine correctness bug on the *rename* path: `OnModelCreatingRewriter
      .RenameEntityReferences` (called by `DiagramEditor.RenameEntity`)
      renamed `Entity<T>()`/`DbSet<T>`/`IEntityTypeConfiguration<T>`
      references but never touched `new OldName { ... }` object-creation
      expressions inside `HasData(...)` seed rows for that entity — so
      renaming an entity with seed data produced regenerated source that no
      longer compiled (referencing a deleted class). Fixed by extending
      `RenameEntityReferences` to also find and rename any
      `ObjectCreationExpressionSyntax` whose type matches the old entity name
      within a `HasData(...)` call's argument list, alongside the existing
      reference-renaming passes. Entity *removal* needed no code change: the
      whole `Entity<T>(...)` block (including any of that entity's own
      `HasData` calls) is already deleted wholesale; seed data on *other*
      entities that may reference the removed entity's key values is a
      data-value concern the parser can't verify (it doesn't model seed row
      contents), so the pre-existing generic diagnostic remains the "warn"
      for that half, as the backlog item allowed. Property rename/remove
      leaving stale `HasData` member-initializer references is a related but
      distinct gap, out of scope here (the item was scoped to entity
      remove/rename). 567/567 tests green across all three test projects.
- [x] **`[found]` Smaller unread config:** `HasQueryFilter`, `HasComment`,
      `IsUnicode`/`IsFixedLength`, `UseCollation`, `ToJson`, temporal tables
      (`ToTable(b => b.IsTemporal())`), table/entity splitting
      (`SplitToTable`), `[InverseProperty]`, `[DeleteBehavior]`. Individually
      niche; the Priority 1 diagnostic covers them collectively until any
      earns a parser.
      **Update:** All eight real constructs now parse + merge into
      `EntityModel`/`PropertyModel` (`HasQueryFilter` →
      `EntityModel.HasQueryFilter`; `HasComment` (entity + property) →
      `EntityModel.Comment`/`PropertyModel.Comment`; `IsUnicode`/
      `IsFixedLength` → `PropertyModel.IsUnicode`/`IsFixedLength`;
      `UseCollation` → `PropertyModel.Collation`; `ToJson` →
      `EntityModel.IsJson`/`JsonColumnName`; `ToTable(b => b.IsTemporal())` →
      `EntityModel.IsTemporal`; `SplitToTable` (secondary table name only,
      not the builder lambda's per-property assignment) →
      `EntityModel.SplitTables`; `[InverseProperty]` →
      `PropertyModel.InverseProperty`) — see
      `2026-07-21-smaller-unread-config-design.md`. All are recognized by
      `FluentConfigParser.RecognizedCallNames` so they no longer trip
      `UnrecognizedConfigCall`. `[DeleteBehavior]` is not a real EF Core
      attribute and was dropped from scope. Parse + merge only, matching the
      precedent set by relationships/value-generation — no rewriter,
      `DiagramEditor` method, or diagram UI for any of the eight. Round-trip
      fuzz corpus extended to cover all eight together. 603/603 tests green
      across all three test projects.

## Priority 4 — App-level features

- [x] **`[found]` Keyboard shortcuts for undo/redo.** Ctrl+Z/Ctrl+Y (outside
      the textareas) should drive the `DiagramEditor` undo stack; buttons only
      today. Needs a small JS keydown interop that ignores events targeting
      inputs/textareas.
      **Update:** Added `wwwroot/js/keyboardShortcuts.js`
      (`registerUndoRedoShortcuts`/`unregisterUndoRedoShortcuts`), a
      `document`-level `keydown` listener that ignores events whose target is
      an `INPUT`/`TEXTAREA`/`contentEditable` element, and otherwise maps
      Ctrl/Cmd+Z to undo and Ctrl/Cmd+Y or Ctrl/Cmd+Shift+Z to redo (the Mac
      convention), reusing the existing `OnUndoShortcut`/`OnRedoShortcut`
      call into `UndoAsync`/`RedoAsync`. `Home.razor` now implements
      `IAsyncDisposable`, registers a `DotNetObjectReference<Home>` on first
      render, and exposes the two `[JSInvokable]` callbacks; disposal
      unregisters the listener and disposes the reference. Button titles and
      the in-app "How to edit the diagram" legend now mention the shortcuts.
      524/524 + 78/78 tests still green (Core + Web). Verified the script is
      correctly served and wired into `index.html` via a running `dotnet run`
      dev server; genuine keydown-in-browser verification wasn't possible —
      no browser or Playwright-installable environment is available in this
      sandbox (no `chromium`/`chromium-cli` binary, no `pwsh` to run
      `playwright.ps1 install`), matching the same limitation noted on
      earlier browser-verification passes in this backlog.
- [x] **`[spec]` Auto-layout.** Entities land on a fixed grid; no
      layered/force-directed layout, no zoom-to-fit, no minimap. Biggest
      quality-of-life gap for models above ~10 entities.
      **Update:** Added `DiagramAutoLayout.Apply` (new
      `src/EfSchemaVisualizer.Web/Diagram/DiagramAutoLayout.cs`): a layered
      layout, not force-directed — entities are assigned a layer via
      longest-path-from-root over dependent→principal relationship edges
      (principals land left of their dependents), with cycles (self-refs,
      mutual FKs) broken by ignoring any edge back to an entity still on the
      current DFS stack so every entity still gets a finite layer; within a
      layer, entities are ordered by a single barycenter pass against the
      layer above (falling back to declaration order) to cut down on crossing
      relationship lines. Node width/height come from each `EntityNodeModel`'s
      real, already-rendered `Size` (nullable pre-render; falls back to a
      260x160 default) rather than a fresh measurement pass, so it's a pure,
      unit-testable function over `BlazorDiagram`'s existing node/link state.
      Wired to a new "Auto-layout" toolbar button in `Home.razor` (existing
      node positions are left untouched otherwise, so manual dragging isn't
      fought — it's a manual "arrange" action, not automatic on every edit),
      which also calls the library's existing (but previously unused)
      `BlazorDiagram.ZoomToFit`; a separate "Zoom to fit" button exposes that
      independently for re-centering after manual drags. Minimap: Z.Blazor.Diagrams
      already ships a `NavigatorWidget` (overview/navigator) that was simply
      unused; added it into `DiagramCanvas`'s `Widgets` render fragment,
      picking up `BlazorDiagram` from the existing `CascadingValue` for free.
      9 new tests in `DiagramAutoLayoutTests.cs` (layering over chains/isolated
      entities/self-refs/cycles/dangling references, positioning: principal-
      before-dependent, same-layer stacking without overlap, no-measured-size
      fallback, empty-diagram no-op). 612/612 tests green across all three
      test projects. Real in-browser verification of the button/drag/minimap
      interaction wasn't possible — no browser or Playwright-installable
      environment in this sandbox, the same limitation noted on every prior
      browser-verification pass in this backlog.
- [x] **`[found]` Diagram layout isn't persisted.** Node positions are lost on
      reload/re-render from scratch. Persist to localStorage keyed by source
      hash, and/or a sidecar JSON in the downloaded zip that re-upload reads.
      **Update:** Did both. Added `EfSchemaVisualizer.Core.Archive.EntityPosition`
      and an optional `layout` parameter to `ProjectArchiveWriter.Write`, which
      writes it as a `diagram-layout.json` zip entry (only when non-empty, so
      existing two-entry zips are unaffected); `ProjectArchiveReader.Read`
      recognizes that entry by name (ahead of the `.cs`-only filter) and
      returns it via a new `ProjectArchiveResult.Layout`, tolerating malformed
      JSON by dropping it silently rather than raising a diagnostic — layout is
      display-only, not part of the schema-correctness trust story the other
      diagnostics protect. Added a new pure `EfSchemaVisualizer.Web.Diagram.
      DiagramLayout.Capture`/`Apply` (reads/writes `EntityNodeModel.Position`
      by entity name, mirroring `DiagramAutoLayout`'s existing pattern of pure
      functions over a live `BlazorDiagram`). For the "re-render from scratch"
      case, `Home.razor` now hashes `ClassSource`/`ConfigSource` (SHA-256) as a
      localStorage key: a new `wwwroot/js/layoutStorage.js`
      `saveDiagramLayout`/`loadDiagramLayout` pair persists/restores captured
      layouts keyed by that hash, saved on every node-drag (`NodeModel.Moved`,
      wired via a new `diagram.Nodes.Added` handler alongside the existing
      `Links.Added` one), every diagram gesture (`OnDiagramEditedAsync`), and
      Auto-layout; restored automatically at the end of `RenderDiagramAsync`
      when the current source matches a previously-seen hash. Since every
      gesture changes the source text and therefore the hash key, `
      layoutStorage.js` keeps a small LRU index (25 entries) to bound
      localStorage growth over a long session rather than accumulating
      entries forever. For the zip round trip, `DownloadZip` now captures and
      includes the live layout; `OnZipSelected` applies an uploaded zip's
      `Layout` on top of the freshly rendered diagram (and re-saves it to
      localStorage), so a zip carries its own layout independent of whether
      the browser has seen that exact source before. 11 new tests across
      `ProjectArchiveWriterTests`/`ProjectArchiveReaderTests`/
      `ProjectArchiveRoundTripTests` (Core.Tests) and a new
      `DiagramLayoutTests.cs` (Web.Tests); 642/642 tests green across all
      three test projects. Verified `layoutStorage.js` is correctly served and
      wired into `index.html` via a running `dotnet run` dev server; genuine
      drag-and-persist in-browser verification wasn't possible — no browser or
      Playwright-installable environment in this sandbox (no
      `chromium`/`chromium-cli` binary, no `pwsh`), the same limitation noted
      on every prior browser-verification pass in this backlog.
- [x] **`[found]` No diagram export.** No PNG/SVG/Mermaid export — the obvious
      "share with the team" feature for a *visualizer*. Mermaid `erDiagram`
      text output is the cheapest first step (pure string generation from
      `DiagramModelResult`, trivially testable).
      **Update:** Scoped to SVG + Mermaid only (PNG explicitly deferred).
      Added `src/EfSchemaVisualizer.Web/Diagram/MermaidExporter.cs` — pure
      string generation over `DiagramModelResult` alone (no live diagram
      needed), emitting `erDiagram` syntax with cardinality tokens per
      `RelationshipModel.Kind`, PK/FK attribute markers (PK from
      `EntityModel.KeyPropertyNames`, FK from any relationship's
      `ForeignKeyProperties` where the entity is the dependent), and a
      `SanitizeType` helper so generic/nullable CLR types (`List<string>`,
      `int?`) become valid Mermaid attribute tokens. Added
      `src/EfSchemaVisualizer.Web/Diagram/SvgExporter.cs` — a from-scratch SVG
      renderer, not a live-DOM capture: investigation found the on-screen
      canvas is a hybrid (entity cards are HTML `<div>`s in one absolutely-
      positioned layer, relationship lines are real `<svg>` in a sibling
      layer), so there's no single live element that's already valid
      standalone SVG. Instead it reads each `EntityNodeModel`'s real
      `Position`/`Size` from the live `BlazorDiagram` (the same node state
      `DiagramAutoLayout.Apply` already reads, falling back to the same
      260×160 default pre-render) and draws `<rect>`/`<text>` cards and
      `<line>` relationship connectors anchored to whichever side of each box
      faces the other entity. Both are wired to new "Export SVG"/"Export
      Mermaid" toolbar buttons in `Home.razor`, reusing the existing
      `downloadFileFromStream` JS helper (`wwwroot/js/downloadFile.js`), which
      gained an optional third `mimeType` parameter (`image/svg+xml` /
      `text/plain`) so the downloaded file gets a real content type instead of
      the generic `application/octet-stream` the `.zip` download path already
      used. 17 new tests across `MermaidExporterTests.cs`/`SvgExporterTests.cs`
      (Web.Tests); 629/629 tests green across all three test projects. Real
      in-browser verification of the toolbar buttons wasn't possible — no
      browser or Playwright-installable environment in this sandbox (no
      `chromium`/`pwsh`), the same limitation noted on every prior
      browser-verification pass in this backlog — but output was manually
      verified by exporting the app's own default Blog/Post sample through
      `DiagramModelBuilder.Build` and checking the resulting Mermaid/SVG text
      directly (correct cardinality token, PK/FK markers, and SVG coordinates
      that place both entity boxes and the connecting line consistently).
- [ ] **`[spec]` Zip round-trip loses file boundaries.** Documented trade-off
      from the original slice: everything collapses to `Entities.cs` +
      `DbContext.cs` and non-`.cs` files are dropped. The real fix — preserve
      per-file boundaries and pass through unrecognized files verbatim — is
      the biggest remaining barrier to "upload your real project".
- [x] **`[found]` Add-property type picker.** `DiagramEditor.AddProperty`
      hardcodes `string`. A small dropdown (string/int/long/decimal/DateTime/
      Guid/bool) on the "+ Add property" row would cover most cases, and the
      double-click type edit already handles the rest.
      **Update:** `DiagramEditor.AddProperty` now takes an optional `clrType`
      parameter (defaults to `"string"`, unchanged for existing callers).
      `EntityNode.razor`'s "+ Add property" row grew a `<select>` (string/
      int/long/decimal/DateTime/Guid/bool) immediately before the button,
      backed by a new `_addPropertyType` field wired via `OnAddPropertyTypeChanged`.
      No `Core` changes needed — `EntityClassRewriter.AddProperty` already
      took a full `PropertyModel`. 2 new tests in `DiagramEditorTests.cs`
      covering the explicit-type and default-type paths; 631/631 tests green
      across all three test projects.
