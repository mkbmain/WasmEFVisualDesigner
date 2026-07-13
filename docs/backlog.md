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

- [ ] **`[spec/plan]` Blazor WebAssembly shell** referencing `EfSchemaVisualizer.Core`.
- [ ] **`[spec]` Read-only ER diagram render** of parsed `EntityModel`s first.
      (Open risk in `[spec]`: no Blazor canvas/diagramming library chosen yet.)
- [ ] **`[spec]` Editable diagram** wired to the rewriter (depends on Priority 1).
- [ ] **`[spec]` `.zip` upload / download**, fully client-side, stateless.
- [ ] **`[spec]` GitHub Actions → GitHub Pages** deploy on push to `main`.
- [ ] **`[spec]` Roslyn WASM payload size / first-load time** — measure early, flagged as an open risk.

## Priority 5 — Repo hygiene & smaller cleanups

- [ ] **`[found]` No README.** `[spec]` makes the README load-bearing for the
      trust story and star/donation prompts — it's the public front door and is
      currently absent.
- [ ] **`[spec]` MIT license file.**
- [ ] **`[found]` Perf (minor at current scale):** `FluentConfigParser` re-walks
      the whole tree once per distinct entity name (`FluentConfigParser.cs:18-44`,
      O(entities × nodes)); `ModelMerger.ApplyMaxLengths` is O(props × configs)
      via `FirstOrDefault` (`ModelMerger.cs:14`). A single grouped pass / a
      `(entity, property)`-keyed dictionary would fix both.
- [ ] **`[found]` Namespacing:** `ModelMerger` and `MaxLengthConfig` live under
      `Parsing` but are merge/DTO concerns, not parsing.
- [ ] **`[found]` Null-forgiving noise** (`.Distinct()!`, `entityName!`) in
      `FluentConfigParser` — project to a non-null `List<string>` once instead.
- [ ] **`[found]` Widen test surface** with the P0 edge cases: empty/class-less
      file, record entity, `Property("Name")` string overload, non-literal
      `HasMaxLength` arg, renamed builder param, multiple classes per file, and
      the not-yet-built add/remove/rename paths.
