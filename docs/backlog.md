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
- [ ] **`[found]/[plan]` Drop a property** (remove from class and remove any of its config statements).
- [ ] **`[plan]` Rename** an entity or property (class member + every referencing fluent call + lambda body).
- [ ] **`[plan]` Add / remove an entity** — mint a whole new `modelBuilder.Entity<T>(...)` block, or remove one, without disturbing siblings.
- [ ] **`[found]` Remove a fluent config** (e.g. clearing a max length) — the delete counterpart of the insert above.

## Priority 2 — Broaden fluent-config coverage (same pattern as `HasMaxLength`)

> `[plan]` explicitly scopes the spike to `HasMaxLength` only; these follow the
> established parse → merge → rewrite pattern.

- [ ] **`[spec/plan]` `IsRequired` / nullability** as fluent config (distinct from CLR `?`).
- [ ] **`[spec/plan]` Precision / scale** (`HasPrecision`) for decimal.
- [ ] **`[spec]` Keys** — `HasKey`, including composite keys.
- [ ] **`[spec]` Indexes** — `HasIndex`, including unique.
- [ ] **`[spec]` Relationships** — 1:1, 1:many, many:many (`HasOne`/`WithMany`/`HasForeignKey` etc.). Largest single item; likely its own plan.
- [ ] **`[spec]` Column/table mapping** — `ToTable`, `HasColumnName`, `HasColumnType`, default values.
- [ ] **`[found]` Diagnostic codes are bare string literals.** `NoEntityDeclarations`,
      `UnresolvablePropertyName`, and `UnreadableMaxLengthArgument` are currently
      scattered as bare string literals across `EntityClassParser.cs` and
      `FluentConfigParser.cs`, each duplicated in test assertions. A shared
      `DiagnosticCodes` constants class or enum would prevent drift as more
      diagnostic codes are added, but with only three codes and no UI consumer
      yet, this can wait until more codes accumulate or until the Blazor shell
      needs to interpret them.
- [ ] **`[found]` `GetPropertyNameFor` doesn't resolve parenthesized lambdas.** Calls like
      `entity.Property((Person e) => e.Name)` use `ParenthesizedLambdaExpressionSyntax`,
      which are not handled in the `switch` expression — the method falls through
      and emits `UnresolvablePropertyName` rather than resolving "Name". This is
      a plausible shape users could write, but is currently untested. Correct
      behavior is diagnostic (not silent data loss), but resolving it is a future pass.

## Priority 3 — Second config style

- [ ] **`[spec/plan]` `IEntityTypeConfiguration<T>` support.** Fast-follow after
      `OnModelCreating`. Structurally easier (one entity per file), but a
      separate parsing/codegen path.

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
