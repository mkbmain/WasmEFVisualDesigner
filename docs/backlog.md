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

- [ ] **`[found]` Raw exception dumps are shown to the user.** `Home.razor`
      sets `_error = ex.ToString()` (full stack trace) on both the render path
      (`Home.razor:134`) and the zip-upload path (`Home.razor:211`), rendered in
      a red `<pre>`. Show a friendly message; log the detail to the browser
      console instead.

- [ ] **`[found]` Textarea `<label>`s aren't associated with their inputs.**
      `Home.razor:23,27` use bare `<label>Entity classes</label>` with no
      `for`/`id` linkage, so screen readers don't announce them. (The in-diagram
      controls already got `aria-label`s in the last round — this is the one
      spot that was missed.)

- [ ] **`[found]` No undo/redo.** For a visual editor every gesture rewrites the
      source irreversibly; the only "undo" is Ctrl-Z inside a textarea, which
      then desyncs from the diagram. A source-snapshot undo stack in
      `DiagramEditor` would be cheap (it already funnels every mutation through
      `Apply`).

- [~] **`[found]` Diagram gestures are undiscoverable.**
      Nothing on the page explained the interaction model (double-click a
      name/type to edit, drag between ports to draw a relationship, click a
      property to expand options). A short legend or first-run hint would help;
      at least document the gestures in the README.
      **Update (2fba175):** the double-click *rename/edit* affordance is now
      obvious — the entity title, property name, and property type carry a
      dashed underline, text cursor, tooltips, and a ✎ pencil on the title, the
      node hint line leads with "Double-click a name or type to edit", and the
      "Table:" field is tooltipped to clarify it sets `.ToTable` rather than the
      class name (the exact mix-up that surfaced this). Still open: the
      drag-to-connect and property-expand/key-toggle gestures have no legend
      beyond the existing one-line connect hint, and none of it is documented in
      the README yet.

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
