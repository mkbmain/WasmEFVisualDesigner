# EF Schema Visualizer — Backlog

> Single source of truth for everything not yet built.
>
> Rounds 1–3 are closed and archived in `docs/DoneBackLog20260723.md`
> (647 tests green at `af42ca7`). This file starts fresh with **Round 4**.
>
> **Legend for source of each item:**
> - `[found]` — discovered during code review, not previously written down
> - `[verified]` — reproduced by executing the pipeline, not inferred from reading
> - `[spec]` — deferred in `docs/superpowers/specs/2026-07-07-ef-schema-visualizer-design.md`
> - `[carried]` — carried over unfinished from the archived backlog

---

# Round 4 review — 2026-07-23

> A fresh audit framed around two end-to-end user journeys the tool is meant to
> serve, rather than around EF feature coverage:
>
> 1. **From scratch** — a DBA with no C# experience designs a schema in the app
>    and ends up with a database they can run.
> 2. **Round trip** — a DBA uploads an existing code-first project, amends it,
>    re-downloads it, and runs it.
>
> **Neither journey completes today.** The parse → edit → regenerate engine
> itself is sound (unrecognized fluent config *is* preserved verbatim through
> edits — that design works). The failures are in the packaging layer around it
> (zip in/out), in a handful of missing input validations, and in the absence of
> EF's convention rules.
>
> Findings marked `[verified]` were reproduced by driving `DiagramEditor` /
> `ProjectArchive*` directly against realistic inputs (scaffolded DbContext,
> multi-file project zip, TPH hierarchy, owned types, convention-only model,
> many-to-many, composite keys). None of the below is started.

---

## Priority 0 — Fatal: crashes, data loss, non-compiling output

- [x] **`[found]/[verified]` F1 — `RemoveEntity` crashes the whole app on
      bare-statement config.** — Fixed 2026-07-23.
      `OnModelCreatingRewriter.RemoveEntity` called `root.RemoveNodes(...)` on
      the inner `ExpressionStatementSyntax` nodes it collected. When the config
      source is bare top-level fluent statements — the shape the app ships as
      its own default sample, and the shape `ProjectArchiveWriter` produces on
      every download — those statements are wrapped in a `GlobalStatementSyntax`,
      so removing the inner statement left a `GlobalStatement` with a null
      child and Roslyn threw `ArgumentNullException`, which escaped
      `EntityNode.razor`'s uncaught call into the Blazor renderer and crashed
      the app with every unsaved edit lost.

      Fix: when the removed node's parent is a `GlobalStatementSyntax`, remove
      that parent instead (root cause). Audited the other 8
      `RemoveNode`/`RemoveNodes` call sites in `OnModelCreatingRewriter`; none
      are reachable with a bare top-level statement, since they only remove
      nodes found inside an `Entity<T>(entity => {...})` lambda block. Added
      `RemoveEntity_BareTopLevelStatementConfig_RemovesStatementWithoutThrowing`
      regression test. Also wrapped every `EditContext.Editor.*` gesture
      handler in `EntityNode.razor`/`RelationshipLinkLabel.razor` in a
      `SafeEdit` helper so any future rewriter exception surfaces as an inline
      error instead of crashing the app, guarded by a markup-source regression
      test (`GestureHandlerSafeEditTests`) that verifies every call site stays
      wrapped.

- [x] **`[carried]/[verified]` F2 — Download throws away the entire uploaded
      project.** — Partially fixed 2026-07-23 (scoped fix; see note below).
      `ProjectArchiveReader` already collected `PassthroughFiles`,
      `EntityFileOrigins`, and `ConfigFileOrigins` (commit `af42ca7`), but
      nothing consumed them — `ProjectArchiveWriter.Write` hardcoded exactly
      two output entries regardless of what was uploaded, so the `.csproj` and
      every other non-`.cs` file (migrations, `Program.cs`, `appsettings.json`)
      vanished on download, leaving nothing to run `dotnet ef migrations add`
      against.

      Fix (scoped, chosen over the full per-file architecture below):
      `ProjectArchiveWriter.Write` now re-emits every `PassthroughFiles` entry
      verbatim at its original path, and — when a project has exactly one
      class file and/or exactly one config file (the common case) — writes the
      current edited source back under that file's original name/path instead
      of `Entities.cs`/`DbContext.cs`. `Home.razor` threads `EntityFileOrigins`
      /`ConfigFileOrigins`/`PassthroughFiles` from the upload through to
      download, clearing them whenever the diagram is (re-)rendered from
      freehand pasted text rather than an uploaded zip. Measured against the
      same 7-file zip: `MyApp.csproj`, `Program.cs`, `appsettings.json`, and
      `Migrations/20240101_Init.cs` now survive download unchanged; the two
      class files (`Entities/Customer.cs`, `Entities/Order.cs`) still collapse
      into one `Entities.cs`, since that multi-file case is F3, not F2, and
      remains open (see below).

      **Not done — deferred by explicit user decision:** the backlog's
      originally-recommended "real per-file round-trip" (teaching
      `DiagramEditor` to track per-file state and route cross-file-aware edits
      like entity rename to the correct originating file) was assessed as a
      large architectural change — DiagramEditor's ~30 edit methods, undo/redo
      snapshots, and every place a rename must scan *other* files for
      references — and deferred in favor of the scoped fix above. That full
      fix is still what F3 needs.

- [x] **`[carried]/[verified]` F3 — The regenerated `Entities.cs` does not
      compile.** — Fixed 2026-07-23.
      `ProjectArchiveReader.cs:81-82` joined every class file with a blank line
      into one blob. Two entity files each carrying their own `using` block and
      file-scoped namespace concatenated into a single illegal compilation unit:

      ```
      concatenated multi-file class source: 1 syntax error
          CS1529 A using clause must precede all other elements
                 defined in the namespace ... @ line 9
      ```

      Multiple `namespace X;` declarations in one file are also illegal, and all
      types silently collapsed into whichever namespace landed first — changing
      every type's fully-qualified name. Any project with more than one entity
      file produced broken output.

      Fix: `MultiFileSourceMerger.Merge` now folds every uploaded class/config
      file into one valid compilation unit at read time (deduplicating `using`
      directives and reconciling namespaces), and `MultiFileSourceMerger.Split`
      reverses that at write time, routing the edited merged source back to
      each file's original path via `ProjectArchiveReader`/`ProjectArchiveWriter`.
      `DiagramEditor` now owns `EntityFileOrigins`/`ConfigFileOrigins` directly
      and keeps them correctly re-keyed across renames, instead of the caller
      threading stale origins through. Verified end to end: a new
      `ProjectArchiveRoundTripTests` test uploads a 5-file project (two entity
      files in their own namespace, two config files, a `.csproj`), renames an
      entity through `DiagramEditor`, downloads, and asserts every downloaded
      `.cs` file parses with zero Roslyn error diagnostics — the same
      verification methodology this finding was originally reproduced with.

      **Documented limitations (not defects):** all of a merged document's
      `using` directives are re-emitted into every split file — a safe
      over-approximation, since an unused `using` warns rather than fails to
      compile. A model-level (non-entity-scoped) config statement with no
      single resolvable entity falls back to the default config file rather
      than its original one.

- [x] **`[found]/[verified]` F4 — Migrations, `ModelSnapshot`, and `obj/` are
      parsed as entities.** — Fixed 2026-07-23.
      `ProjectArchiveReader.Read` iterated every zip entry with no path
      filtering, so migration classes, `AppDbContextModelSnapshot.cs`, and
      `obj/`/`bin/` build output all got parsed as entities and folded into the
      downloaded `Entities.cs`.

      Fix: new `ArchivePathFilter` classifies each entry before it reaches
      class/config classification. `bin/`/`obj/` path segments are dropped
      entirely (regenerable build output; not written to the download either,
      shrinking upload/download bulk as the P4 size-ceiling item anticipated).
      `Migrations/` path segments and `*ModelSnapshot.cs`/`*.Designer.cs`/
      `*.g.cs` filenames are excluded from diagram parsing but preserved
      verbatim via `PassthroughFiles`, so they still round-trip unchanged on
      download (needed for `dotnet ef` to keep working) without rendering as
      fake entities. Both categories now surface as diagnostics
      (`ArchiveBuildArtifactSkipped`, `ArchiveGeneratedFileExcluded`) instead
      of silently. Verified with new `ProjectArchiveReaderTests` cases (one per
      filtered path/suffix, plus one confirming bin/ files are dropped from
      passthrough too) and a `ProjectArchiveRoundTripTests` case uploading a
      project with a migration + snapshot file, confirming both are absent
      from `ClassSource` and byte-identical in the downloaded zip.

- [x] **`[found]/[verified]` F5 — Renaming to a C# keyword corrupts the source.** — Fixed 2026-07-24.
      `DiagramEditor.cs:51` guards with `SyntaxFacts.IsValidIdentifier`, which
      validates lexical identifier *shape* and therefore accepts reserved words.
      Renaming `Blog` → `class` produced:

      ```csharp
      public class class            // from RenameClass
      { ... }

      public class Post
      {
          public class Blog          // was: public Blog Blog { get; set; } = null!;
          {
              get ; set ; } =  null  ! ;   // source destroyed
      }
      ```

      The follow-up `RenamePropertyTypeReferences` reinterprets the navigation
      property as a nested class declaration. Recoverable only via Undo.
      `RenameProperty` (`DiagramEditor.cs:94`) had the same gap and additionally
      permitted a property named identically to its enclosing type (CS0542).

      Fix: `RenameEntity` and `RenameProperty` now both reject
      `SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None` (reserved keywords
      only — contextual keywords like `var`/`async` remain valid identifiers and
      still work), and `RenameProperty` additionally rejects a new property name
      equal to its entity's name. Added
      `RenameEntity_ToReservedKeyword_Fails`, `RenameProperty_ToReservedKeyword_Fails`,
      and `RenameProperty_ToSameNameAsEnclosingEntity_Fails` regression tests in
      `DiagramEditorTests`.

- [x] **`[found]/[verified]` F6 — The "Default value" field emits raw unquoted
      text.** — Fixed 2026-07-24.
      `DiagramEditor.SetDefaultValue` (`DiagramEditor.cs:857`) validated only that
      the text parses as *some* C# expression, so what a DBA would naturally type
      produced non-compiling source:

      ```csharp
      entity.Property(e => e.Title).HasDefaultValue(GETDATE())   // won't compile
      entity.Property(e => e.Title).HasDefaultValue(Unknown)     // won't compile
      ```

      They had to know to type `"Unknown"` with C# quotes, and nothing in the UI
      said so.

      Fix: `DiagramEditor.SetDefaultValue` now interprets the field by the
      property's CLR type — for `string`/`Guid`/`DateTime`/`DateTimeOffset`/
      `DateOnly`/`TimeOnly` it auto-quotes plain text into a proper C# string
      literal (idempotent if the text is already a valid quoted literal), and for
      every other type it still requires an actual C# literal (numeric/bool/null/
      char), rejecting identifiers or invocations like `GETDATE()` with an error
      pointing at the new field below instead of writing uncompilable source.
      Also added a separate **Default value SQL** field/gesture wired end-to-end
      to `HasDefaultValueSql` — new `PropertyModel.DefaultValueSql`,
      `FluentConfigParser.ParseDefaultValueSqls` (+ recognized-call-name entry +
      `UnreadableHasDefaultValueSqlArgument` diagnostic), `ModelMerger.
      ApplyDefaultValueSqls`, `OnModelCreatingRewriter.SetDefaultValueSql`/
      `RemoveDefaultValueSql` (built from the existing generic string-arg
      helpers, same as `HasColumnType`), `DiagramEditor.SetDefaultValueSql`, and
      an `EntityNode.razor` input wired through `SafeEdit` — which is the call
      the SQL-shaped input actually wants. Covered by new tests across all five
      layers (parser, merger, rewriter, editor, and the existing markup-source
      `SafeEdit`-coverage test, which the new gesture handler satisfies without
      changes).

## Priority 1 — Models that render *wrong*, with no warning

> Worse than a missing feature: the DBA has no way to tell the diagram is lying.

- [x] **`[found]/[verified]` W1 — No EF conventions are applied.**
      The parser reads only what is written explicitly. Verified against a
      perfectly ordinary convention-based model (`int Id`, `Customer Customer`,
      `int CustomerId`, no fluent config, no attributes):

      ```
      Convention-only entities:       Customer key=[]  |  Order key=[]
      Convention-only relationships:  (none)
      Diagnostics:                    (none)
      ```

      Two keyless, unrelated boxes for a model that EF maps with a PK on `Id` and
      a one-to-many FK. Any convention-based project — a large share of real ones
      — renders as a disconnected pile. This is the biggest single "your real
      project renders wrong" gap remaining.

      Minimum fix: infer `Id` / `<Type>Id` as the primary key, and infer a
      relationship from navigation-property + `<Nav>Id`/`<Principal>Id` pairs.
      Render convention-derived keys/relationships distinctly (e.g. dashed) so
      the user can see what is explicit and what is inferred, and never write an
      inferred value back to source unless the user edits it.

      **Fixed 2026-07-24.** Added `EfSchemaVisualizer.Core.Inference.ConventionInference`,
      invoked from `DiagramModelBuilder.Build` after all explicit config is merged, so it
      only fills gaps and is recomputed fresh on every parse. `InferKey` infers `Id` (case-
      insensitive, wins on conflict) or `<TypeName>Id` as the primary key when no explicit
      `HasKey`/`[Key]`/`HasNoKey`/`[Keyless]` is present. `InferRelationships` matches a
      navigation property against a same-entity `<NavName>Id`/`<PrincipalTypeName>Id`
      property (both required — FK-alone inference was scoped out to keep false-positive
      risk low) and resolves one-to-many/one-to-one via the same principal-back-reference
      scan `EntityClassParser` already uses for `[ForeignKey]`-annotated relationships.
      Both are exposed via new `EntityModel.IsKeyInferred`/`RelationshipModel.IsInferred`
      flags (default `false`, additive) and rendered distinctly — a muted key marker in
      `EntityNode.razor`, a muted link color in `DiagramSync.cs` — so the user can always
      tell explicit from inferred. `DiagramEditor.SetRelationshipShape` previously failed
      outright on an inferred relationship (nothing in source to remove yet); it now
      materializes explicit fluent config on first edit instead, which is the concrete
      mechanism behind "never write an inferred value back to source unless the user edits
      it". Verified against the exact convention-only repro this finding was originally
      written from: `Customer`/`Order` with `int Id`, `Customer Customer`, `int
      CustomerId`, no fluent config, no attributes now render as two keyed, related
      entities instead of two disconnected keyless boxes. Composite convention keys, FK-
      alone relationship inference, and actually suppressing an inferred relationship
      remain out of scope (see the design spec).

- [x] **`[found]/[verified]` W2 — Inheritance renders as unrelated fragments.**
      A TPH hierarchy parses as:

      ```
      Person(Id,Name) key=[Id] | Student(Course) key=[] | Teacher(Salary) key=[]
      ```

      Derived types don't inherit `Id`/`Name`, have no key, and have no link to
      the base. `HasDiscriminator`/`HasValue` are flagged `UnrecognizedConfigCall`
      but nothing indicates the three are one table. At minimum: read base types
      from the class declaration, fold inherited properties into derived
      entities, and draw an inheritance edge. TPT/TPC and discriminator editing
      can follow.

      **Fixed 2026-07-24.** Added `EntityModel.BaseEntityName` and
      `PropertyModel.DeclaringEntityName` model fields. `EntityClassParser`
      resolves `BaseEntityName` from a class's base-list when it matches a
      sibling entity in the same parse. New `Core.Inference.InheritanceInference.Fold`
      module folds inherited properties and keys into derived entities
      (nearest-ancestor-wins on name collisions across multi-level chains) and
      emits one `RelationshipModel` per derived entity with the new
      `RelationshipKind.Inheritance`. Wired into `DiagramModelBuilder.Build` right
      after key inference. `DiagramEditor`'s single-scalar-property edit methods
      (rename, retype, remove, column name/type, max length, required, row
      version, concurrency token, precision, default value, default value SQL,
      key toggle) now resolve which entity actually declares a given property
      (via `DeclaringEntityName`) and route edits there — so editing an inherited
      property from the derived entity's card correctly rewrites the base class's
      source. Rendering: inheritance edges get a distinct link color and label,
      and a read-only expanded view (no FK/cardinality/remove controls, since
      there's no rewriter support for un-inheriting). Verified end-to-end against
      a three-level TPH hierarchy (`Student` and `Teacher` deriving from `Person`)
      with multi-level property inheritance and overlapping property names.
      TPT/TPC mapping strategies, `HasDiscriminator`/`HasValue` parsing/editing,
      removing an inheritance edge, and composite index/alternate-key ownership
      routing across inheritance boundaries remain out of scope (see the design
      spec at `docs/superpowers/specs/2026-07-24-inheritance-tph-design.md`).

      **Known low-harm side effect:** since convention-relationship inference
      (W1) runs on post-fold entities, a derived entity that inherits a
      navigation+FK pair from its base now gets its own inferred relationship
      edge alongside the base's — e.g. `Person.Company`/`CompanyId` produces
      both `Company→Person` and `Company→Student`. This is a defensible read
      of TPH (the derived rows do share that FK column), renders muted-gray
      like any other inferred edge, and isn't tested; revisit if it proves
      confusing in practice.

- [x] **`[found]/[verified]` W3 — Owned types render as their own tables.**
      `OwnsOne(e => e.ShippingAddress, ...)` is flagged unrecognized, but the
      owned `Address` class is still parsed from the class file and drawn as a
      standalone table with its own columns. The DBA sees a table that does not
      exist in the database. At minimum, suppress the standalone node and show
      the owned columns inline on the owner with a diagnostic; full `OwnsOne`/
      `OwnsMany` parsing is the real fix (see P2).

      **Fixed 2026-07-24.** Added `PropertyModel.IsOwned`, `PropertyModel.OwnerNavigationProperty`,
      `EntityModel.IsOwned`, and new `RelationshipKind.Owned` model fields. `FluentConfigParser.ParseOwnedTypeCalls`
      detects `OwnsOne`/`OwnsMany` calls; builder-lambda config that isn't parsed is flagged via new
      `DiagnosticCodes.OwnedNestedConfigIgnored` diagnostic instead of silently dropped. New
      `Core.Inference.OwnedTypeInference.Fold` module folds `OwnsOne` targets' properties inline
      into the owner (target entity removed from the diagram entirely), and marks `OwnsMany`
      targets `IsOwned=true` while keeping them standalone with a new `Owned`-kind relationship.
      Wired into `DiagramModelBuilder.Build` before key and inheritance inference run, so a
      folded-away owned entity never picks up a spurious inferred key. Rendering:
      `RelationshipLabels.For(Owned)` returns "◆", `DiagramSync` gives `Owned` edges a distinct
      color (`#8a6a4a`), `EntityNode.razor` groups folded owned properties under a read-only
      sub-header per navigation property, and `RelationshipLinkLabel.razor` shows a read-only
      "X owns Y via Z" line for `Owned` relationships. Both `OwnsOne` folding and `OwnsMany`
      marking were verified against the exact repro this finding was originally written from:
      `Order.ShippingAddress` (type `Address`, via `OwnsOne`) no longer renders `Address` as a
      standalone entity; `Order`'s card shows the folded properties instead. Nested builder-lambda
      config parsing, editing owned properties, table splitting via `OwnsOne(...).ToTable(...)`,
      `WithOwner` customization, and `ComplexProperty`/EF7+ complex types remain out of scope
      (see the design spec at `docs/superpowers/specs/2026-07-24-owned-types-design.md`).

      **Known low-harm side effect:** since convention-relationship inference (W1) runs on
      post-fold entities, an `OwnsOne` target that itself has a foreign key to a third entity
      (e.g. `Address` has a `Country`/`CountryId` nav+FK pair) can produce a spurious inferred
      relationship edge after its properties fold into the owner — e.g. a `Country→Order` edge
      inferred from the folded-in `CountryId`, even though `Order` never configured that
      relationship itself. This is directly analogous to the W2 side effect above: a defensible
      read of the folded data, renders muted-gray like any other inferred edge, and isn't tested;
      revisit if it proves confusing in practice.

- [x] **`[found]/[verified]` W4 — Model-level config is invisible, with no
      diagnostic.** — Fixed 2026-07-24.
      `FluentConfigParser.ParseUnrecognizedCalls` only walks calls *inside* an
      `Entity<T>()` scope (`FluentSyntaxHelpers.FindConfigurationScopes`), so
      anything hung off `modelBuilder` directly was neither parsed nor flagged
      — e.g. `modelBuilder.HasDefaultSchema("sales")` (every table's schema
      wrong, no warning), `modelBuilder.HasSequence<int>(...)`,
      `modelBuilder.ApplyConfigurationsFromAssembly(...)`.

      Fix: new `FluentSyntaxHelpers.FindModelLevelCalls` finds every invocation
      called directly on the `ModelBuilder` receiver (found via the
      `OnModelCreating(ModelBuilder ...)` parameter name, or via any
      `receiver.Entity<T>(...)` call's receiver for a bare fluent-config
      source with no `OnModelCreating` method) — as opposed to one chained
      onto an `Entity<T>()` result, which `ParseUnrecognizedCalls` already
      covers. New `FluentConfigParser.ParseUnrecognizedModelLevelCalls` flags
      anything there whose name isn't `Entity`/`Ignore`/`HasDefaultSchema` with
      `UnrecognizedConfigCall` (`EntityName: null`, since it's model-wide, not
      entity-scoped). New `FluentConfigParser.ParseDefaultSchema` reads
      `HasDefaultSchema`'s string-literal argument (`UnreadableHasDefaultSchemaArgument`
      diagnostic if it isn't one); `ModelMerger.ApplyDefaultSchema` fills it
      into every entity that didn't already get an explicit schema from
      `ToTable`/`ToView` (those always win as a per-entity override). Wired
      into `DiagramModelBuilder.Build` after table/view mapping so the
      explicit-wins ordering holds. Verified: a model with `HasDefaultSchema
      ("sales")` and one entity explicitly `ToTable("Orders", "audit")` now
      renders the explicit entity with schema `audit` and every other entity
      with schema `sales`; a bare `modelBuilder.HasSequence<int>(...)` now
      emits `UnrecognizedConfigCall` instead of vanishing silently.

      **Not done — separate backlog item:** walking into recognized calls'
      lambda arguments (e.g. `ToTable(t => t.HasCheckConstraint(...))`, whose
      builder-lambda body still isn't read) is a distinct, larger change and
      remains covered by the "SQL-shaped mapping" item under Priority 2, which
      already lists `HasCheckConstraint` as its own parser gap.

- [x] **`[found]` W5 — No EF-validity diagnostics at all.** — Fixed 2026-07-25.
      All 33 codes in `DiagnosticCodes.cs` meant "I couldn't read this syntax".
      Nothing meant "your model is invalid".

      Fix: new `DiagnosticCategory` enum (`Parse` / `ModelValidity`) added to
      `Diagnostic` (default `Parse`, so none of the ~30 existing call sites
      needed to change), and a new `EfSchemaVisualizer.Core.Validation.
      ModelValidityChecker` (parallel to `Inference`/`Merging`) that runs last
      in `DiagramModelBuilder.Build`, once, against the fully-resolved
      `EntityModel`/`RelationshipModel` lists — after all parsing, merging, and
      inference stages. Six checks implemented: entity with no key and not
      `HasNoKey`/`[Keyless]`/owned (`EntityHasNoKey`); two properties mapped to
      the same column (`DuplicateColumnName`); `HasPrecision`/scale set on a type
      that doesn't support it — `decimal` for both, temporal types
      (`DateTime`/`DateTimeOffset`/`TimeSpan`/`TimeOnly`) for precision only
      (`PrecisionOrScaleOnUnsupportedType`); an index naming a property that no
      longer exists on the entity, e.g. after a rename (`IndexReferencesMissingProperty`);
      a foreign key targeting a keyless principal (`ForeignKeyTargetsKeylessPrincipal`);
      `IsRequired(false)` on a non-nullable CLR value type (`IsRequiredFalseOnNonNullableProperty`).
      `Home.razor` now renders model-validity diagnostics in their own "Model
      problems" panel (crimson), separate from the existing orange parse-
      diagnostics panel. Verified with 15 new tests in
      `DiagramModelBuilderValidityTests.cs`, one positive/negative pair per
      check; full suite (639 Core + 172 Web) green, no existing test needed a
      fixture change.

      **Two checks deliberately reworded from how this item originally described
      them, because the literal wording described EF behavior that doesn't
      actually throw:** "`IsRequired()` on a nullable CLR property" is legal EF
      usage (it makes the column stricter than the CLR type) — the actual
      model-build error is the reverse, `IsRequired(false)` on a CLR type that
      can *never* be null (checked only against a known set of built-in value
      types, to avoid false positives on `string`/reference-type nullability,
      which depends on project-wide NRT settings this model can't see).
      "FK targeting a non-key/non-alternate-key" was narrowed to "FK targeting a
      keyless principal", since `RelationshipModel` has no field recording which
      principal property an FK targets — `HasPrincipalKey` isn't parsed yet (see
      Priority 2), so today every relationship implicitly targets the
      principal's own key, and the only detectable failure of that assumption is
      a keyless principal.

      **Not done — deferred, scoped out to avoid false positives:** "entity with
      no `DbSet` and no `Entity<T>()` registration" was dropped from this pass.
      EF actually includes an entity in the model via three routes, not two —
      `DbSet<T>`, explicit `Entity<T>()`, *or* navigation-property reachability
      from an already-registered entity (e.g. `Order` reachable via
      `Customer.Orders` with no `DbSet<Order>` at all is valid, convention-based
      EF). Implementing the check as literally described would have false-
      positived on that common, legitimate shape — and on this project's own
      test fixtures, most of which have no `DbContext`/`DbSet` at all. A correct
      version needs a reachability graph from registered roots, which is a
      bigger, separate change.

## Priority 2 — EF surface not parsed at all

> All of these are correctly *preserved* in the source through edits (the
> rewriter is surgical), and all now fire `UnrecognizedConfigCall` — but none is
> shown in the diagram or editable. Ordered by how much a **database designer**
> would miss it.

- [x] **`[found]` SQL-shaped mapping the DBA will look for first.**
      `HasDefaultValueSql` and `HasDefaultSchema` were already implemented.
      `HasConstraintName` (FK name) and `HasName` / `HasDatabaseName` (PK and
      index constraint names) are now parsed, modeled, editable, and rewritten
      (see `docs/superpowers/plans/2026-07-25-constraint-naming-plan.md`).

      **Fixed 2026-07-28.** Added full parse/model/edit/rewrite/UI support for
      `HasComputedColumnSql`, `HasCheckConstraint`, and `HasSequence` /
      `UseSequence`. New `PropertyModel.ComputedColumnSql` for computed columns
      (parsed by `FluentConfigParser.ParseComputedColumnSqls`, merged by
      `ModelMerger.ApplyComputedColumnSqls`, rewritten by
      `OnModelCreatingRewriter.SetComputedColumnSql` / `RemoveComputedColumnSql`,
      edited via `DiagramEditor.SetComputedColumnSql`, and UI-exposed in
      `EntityNode.razor` as "Computed column SQL"). New entity-level
      `CheckConstraintModel` (`EntityModel.CheckConstraints`) with
      parsing/merging/rewriting (parser: `ParseCheckConstraints`; merger:
      `ApplyCheckConstraints`; rewriter: `SetCheckConstraint` /
      `RemoveCheckConstraint`; editor: `AddCheckConstraint` /
      `SetCheckConstraint` / `RemoveCheckConstraint`; UI: `EntityNode.razor`
      "Check constraints" list). New model-level `SequenceModel` with
      parse/merge/rewrite entry points mirroring check constraints (parser:
      `ParseSequences`; merger: `ApplySequences`; rewriter: `SetSequence` /
      `RemoveSequence`; editor: `AddSequence` / `SetSequence` /
      `RemoveSequence`), plus `PropertyModel.SequenceName` / `SequenceSchema`
      for per-property `UseSequence()` configuration (editor:
      `DiagramEditor.SetUseSequence`, which internally calls the rewriter's
      `SetUseSequence` / `RemoveUseSequence` to apply or clear a value; UI: a
      single "Uses sequence:" text input backed by a datalist of existing
      sequence names, not a toggle). Verified end-to-end: a model with `HasComputedColumnSql`,
      `HasCheckConstraint`, and `HasSequence` now parses completely,
      round-trips through edits unchanged, and all three surface as editable
      items in the diagram.

      Also fixed a scoping bug discovered during the naming work: `RecognizedCallNames`
      in `FluentConfigParser` is one flat set shared across every fluent-call
      context, so recognizing `HasName` (to support PK/index naming) also
      silently suppressed `UnrecognizedConfigCall` for `HasName` chained
      onto constructs that don't actually read it — e.g.
      `HasAlternateKey(...).HasName("AK_Foo")` or
      `HasSequence(...).HasName(...)`. Previously these fired diagnostics
      alerting the user the construct wasn't understood. New
      `ContextSensitiveCallNames` map — keyed by the *chained* call name (e.g.
      `HasName`), each mapping to the `HashSet` of owner call names it's
      actually recognized under — limits `HasName` recognition to `HasKey`
      and `HasIndex`, so `HasAlternateKey(...).HasName(...)` now correctly
      re-surfaces `UnrecognizedConfigCall` diagnostics again. Future
      maintainers extending `ContextSensitiveCallNames` should follow this
      same chained-name-keyed pattern.

      **Documented limitations (not defects):** `HasSequence`'s non-generic
      `Type`-first-argument overload (`HasSequence(Type clrType, string name,
      ...)`) is not parsed — only the generic `HasSequence<T>(...)` and the
      plain `HasSequence(string name, ...)` (no type) overloads are.
      `HasAlternateKey` naming via chained `.HasName(...)` is now correctly
      re-flagged as `UnrecognizedConfigCall` (scoping fix for entity-scoped
      constructs). `HasSequence(...).HasName(...)` is a separate, model-level
      construct not added to the scoping table; it is nonetheless flagged as
      `UnrecognizedConfigCall`, since `ParseSequences`'s chained-tail walk over
      `HasSequence(...)` now has a `default:` case that reports any chained
      call it doesn't recognize (including `.HasName(...)`) — added as part of
      this same fix.
- [x] **`[found]` Owned & complex types:** `OwnsOne`, `OwnsMany`,
      `ComplexProperty`. See W3 — currently actively misleading, not just absent.
      — Fixed 2026-07-28. See
      `docs/superpowers/specs/2026-07-28-owned-and-complex-types-design.md`.
      `ComplexProperty` is now parsed, folded, and rendered with a distinct
      marker (`PropertyModel.FoldKind`: `None`/`Owned`/`Complex`), mirroring
      the existing `OwnsOne` fold; a collection-typed `ComplexProperty` target
      is flagged (`ComplexPropertyCollectionUnsupported`) rather than folded.
      Config chained inside an `OwnsOne`/`OwnsMany`/`ComplexProperty` builder
      lambda (`HasMaxLength`, `HasColumnName`, etc.) is now genuinely parsed
      — the builder lambda is treated as its own configuration scope, so
      every existing per-property extractor picks it up with no new
      extractor code; `OwnedNestedConfigIgnored`/`ComplexNestedConfigIgnored`
      now fire only for the still-unhandled `ToTable`/`WithOwner` calls.
      Folded owned/complex properties are now fully editable
      (rename/retype/remove/attribute edits) in the diagram, including a new
      `OnModelCreatingRewriter.FindOrCreateOwnedConfigScope` rewriter
      primitive and a `ValidateOwnedEditDepth` guard that cleanly rejects
      edits on multi-level owned/complex chains (an explicit non-goal)
      instead of corrupting data. Renaming the owner's own navigation
      property now also patches the outer fluent call's lambda parameter.

      **Not done — deferred, candidates for a future pass:** nested
      owned-in-owned/complex-in-complex attribution remains unsupported and,
      per final review, misrenders asymmetrically depending on fold order
      (a spurious standalone card plus a stray class-typed row) rather than
      just "doesn't matter for correctness" as originally assumed; table
      splitting (`OwnsOne(...).ToTable(...)`) and `WithOwner(...)`
      customization inside a builder lambda remain flagged-not-applied; an
      expression-bodied builder lambda (`b => b.SomeCall()`, no braces) still
      isn't parsed as a scope, though it now at least fires a diagnostic
      instead of silently dropping its config; the Phase 3 editor test suite
      has no dedicated `ComplexProperty` structural-edit case (verified
      working manually, `OwnsOne` is well-covered).
- [x] **`[found]` Inheritance:** `HasDiscriminator` / `HasValue`, TPT
      (`UseTptMappingStrategy`), TPC. See W2.
      — Fixed 2026-07-29. See
      `docs/superpowers/specs/2026-07-29-inheritance-mapping-strategy-design.md`.
      `HasDiscriminator<T>("Name")` / `HasDiscriminator("Name")` (implicit-string)
      and chained `HasValue<TDerived>(value)` calls are now parsed, yielding new
      `EntityModel.DiscriminatorPropertyName` / `DiscriminatorClrType` (on hierarchy
      root) and `EntityModel.DiscriminatorValue` (per derived entity), via new
      `FluentConfigParser.ParseDiscriminators` extractor. Similarly, `UseTptMappingStrategy()`
      and `UseTpcMappingStrategy()` are now recognized and yielded into a new
      `MappingStrategy` enum (`Tph` / `Tpt` / `Tpc`), resolved per-hierarchy by
      `InheritanceInference.Fold` with root-priority and reported via a new
      `InconsistentMappingStrategyInHierarchy` diagnostic on conflicts. Strategy
      folding now branches: TPT folds only the inherited primary-key property(ies)
      (rest visible only on ancestor), while TPH/TPC fold all ancestor properties
      (unchanged from before). Editing wired end-to-end: `DiagramEditor.SetMappingStrategy`
      / `SetDiscriminatorColumn` / `SetDiscriminatorValue` / `RemoveDiscriminatorColumn`
      / `RemoveDiscriminatorValue` guard mutually against discriminator ↔ strategy
      conflicts with inline errors (no silent deletion in either direction). Rendering
      shows a mapping-strategy dropdown on every hierarchy member, a discriminator
      summary panel (column name + per-derived-type value mappings, all editable) on
      the root only, and — for TPC — suppresses the inheritance edge in `DiagramSync`
      since TPC has no shared physical table or FK.

      **Documented non-goals (out of scope):** switching strategy never synthesizes
      explicit `ToTable("...")` calls — names follow existing convention-or-explicit-mapping
      logic; only the generic `HasDiscriminator<T>("Name")` and implicit-string
      `HasDiscriminator("Name")` overloads are parsed, not the `Type`-first or
      zero-arg variants; auto-clearing conflicting discriminator or strategy config
      in either direction remains explicitly blocked with an error (not silent deletion),
      per design; combining owned/complex-type folding with mapping-strategy switching
      is untested; diamond/multi-base inheritance chains beyond a linear single-chain
      remain out of scope (see design doc for full list).

      **Known low-harm side effect:** TPT folds only the inherited key property(ies)
      into a derived entity, not other ancestor properties. If a derived entity's own
      fluent config scope references an inherited non-key property directly (e.g.
      `entity.HasIndex(x => x.Name)` where `Name` is declared on the base class), that
      property isn't present in the derived entity's folded property list, so the
      reference can read as a false model-validity diagnostic (e.g. `HasIndex` on an
      inherited property may appear "missing") rather than a real problem with the
      source; narrow edge case (TPT + per-property config on a derived entity
      referencing an inherited non-key property), not fixed this pass.
- [x] **`[found]` Value converters and enums:** `HasConversion` (all overloads),
      `HasConversion<string>()` on enum properties. Enum properties currently
      render as their bare CLR type with no indication of how they're stored.
      — Fixed 2026-07-29. See
      `docs/superpowers/specs/2026-07-29-value-converters-and-enums-design.md`.
      `HasConversion<TProvider>()` and `HasConversion(typeof(TProvider))` (type-only)
      are now fully parsed, modeled, and editable. Lambda-pair conversions
      `HasConversion(convertToProviderExpr, convertFromProviderExpr)` are recognized
      and displayed but read-only. New `PropertyModel` fields: `ConversionProviderClrType`
      (provider type from type-only calls), `ConversionIsCustomLambda` (true for
      lambda-pair calls), `IsEnumType` (true when CLR type matches an enum in
      parsed source), `EnumUnderlyingClrType` (the enum's underlying type, e.g.
      `"int"` or `"byte"`). Parsing via `FluentConfigParser.ParseValueConversions`;
      merging via `ModelMerger.ApplyValueConversions`; inference via `EnumStorageInference.Fold`;
      editing via `DiagramEditor.SetValueConversion` / `RemoveValueConversion` (with
      owned-property variants for folded owned/complex properties). UI adds a
      "Stored as" text input (backed by a datalist of common provider types) per
      property, bound through `SafeEdit` to the editor. When `IsEnumType` is true
      and no explicit conversion is set, a muted hint shows the enum's default
      storage type (e.g. `int (default)` or the actual `EnumUnderlyingClrType`).
      Lambda-pair conversions render as read-only "custom conversion" labels. New
      `UnreadableHasConversionArgument` diagnostic emitted for unrecognized call
      shapes (e.g. `ValueConverter` instance arguments).

      **Documented non-goals (out of scope):** `ValueConverter` instance overloads
      (`new SomeValueConverter()`), `ConverterMappingHints`, inferring a lambda
      conversion's provider type, and editing or removing a lambda-form conversion.
- [x] **`[found]` `HasPrincipalKey`.** Already noted as unsupported in the README;
      relevant now that alternate keys are parsed, since a relationship can
      legitimately target one.
      — Fixed 2026-07-30. See
      `docs/superpowers/specs/2026-07-30-has-principal-key-design.md`.
      `HasPrincipalKey(...)` is now fully parsed (`FluentConfigParser.ParseRelationships`,
      new `RelationshipConfig`/`RelationshipModel.PrincipalKeyProperties` field),
      merged (`ModelMerger.ApplyRelationships`), rewritten
      (`OnModelCreatingRewriter.AppendHasPrincipalKey`), and editable via
      `DiagramEditor.SetRelationshipShape`'s new `newPrincipalKeyProperties`
      parameter, with a matching "Principal key" checkbox list in
      `RelationshipLinkLabel.razor`. New
      `ModelValidityChecker.CheckPrincipalKeyReferencesMissingProperty` flags a
      `HasPrincipalKey` property that no longer exists on the principal entity
      (stale after rename/removal) — deliberately does not require the named
      properties to already form a declared key, since EF implicitly creates
      the alternate key itself when they don't.
- [ ] **`[found]` `UsingEntity`'s nested join-entity configuration.** The join
      entity is read/written; calls chained inside `UsingEntity(j => ...)` are not.
- [ ] **`[found]` `HasData` seed rows.** Flagged and preserved; entity rename now
      patches seed object-creation expressions, but property rename/remove still
      leaves stale member initializers behind (carried over from Round 3 as
      explicitly out of scope there).
- [ ] **`[found]` `ToFunction`, `HasAnnotation`, `HasPartitionKey`,
      provider-specific extensions.** Long tail; the generic diagnostic covers
      them until any earns a parser.
- [ ] **`[found]` String-overload `Entity("Namespace.Type", b => ...)`.** The
      shape EF's own `ModelSnapshot` uses. Verified to parse to nothing today
      (no entities, no diagnostic). Low value on its own, but relevant once F4
      decides what to do with snapshot files.

## Priority 3 — Making "create a database from scratch" actually possible

> Even with every bug above fixed, journey 1 dead-ends. Verified: start from the
> shipped sample, add an entity, download. You get `Entities.cs` (valid C#, no
> namespace) and `DbContext.cs` containing bare `modelBuilder.Entity<Blog>(...)`
> statements — not a DbContext. No class, no `DbSet`s, no
> `using Microsoft.EntityFrameworkCore;`, no `.csproj`, no provider package, no
> connection string, no `Program.cs`, no migration. `modelBuilder` is an
> undefined identifier. Someone with no C# experience cannot turn that into a
> database.

- [ ] **`[found]` Runnable project scaffold on download.** When the session
      didn't originate from an uploaded zip, emit a complete, runnable folder:
      `.csproj` referencing `Microsoft.EntityFrameworkCore.<provider>` +
      `.Design`, a real `AppDbContext : DbContext` with a `DbSet<T>` per entity
      and proper namespaces/usings, `appsettings.json` with a connection-string
      placeholder, and a `README.md` with the three commands
      (`dotnet restore` / `dotnet ef migrations add Init` /
      `dotnet ef database update`). Provider choice (SQL Server / PostgreSQL /
      SQLite) as a dropdown.
- [ ] **`[found]` SQL DDL export.** A DBA would rather read `CREATE TABLE` than
      C#. Pure string generation over `DiagramModelResult`, the same shape as the
      existing `MermaidExporter` and trivially testable. Also doubles as the
      fastest way for a DBA to sanity-check that the diagram matches what they
      meant. Per-provider dialect can start with one and grow.
- [ ] **`[found]` New entities are unusable as minted.** `AddEntity` produces
      `public class NewEntity { }` — no key, no properties. EF refuses to build a
      model with a keyless entity that isn't `HasNoKey`. Mint an `int Id` primary
      key by default, and prompt for the entity name instead of using a
      placeholder.
- [ ] **`[found]` Namespace and `DbSet` name are unreachable.** Neither is
      modelled (`EntityModel` has no `Namespace`) nor editable anywhere in the
      UI. Consequence: renaming an entity does *not* rename its `DbSet` property,
      so in a convention-based project (no `ToTable`) the real table name stays
      the old one while the diagram implies it changed. Model both, expose both,
      and rename the `DbSet` alongside the type.
- [ ] **`[found]` No migration guidance.** The tool never mentions that a table
      or column rename requires `dotnet ef migrations add`, nor that EF may emit
      drop/create rather than rename — which is where real *database* data loss
      happens. Add an explicit warning on rename gestures, and a "what to do
      next" panel after download.

## Priority 4 — Editing-path quality

- [ ] **`[found]` Whole-file `NormalizeWhitespace()` on every rewrite.** Every
      mutator in both rewriters ends with `newRoot.NormalizeWhitespace().ToFullString()`,
      so a single one-field edit reformats the entire file. Verified: comments
      and `#region`s survive, blank lines and multi-line fluent-chain formatting
      do not. For "upload your real project" this means every edit produces a
      whole-file git diff. Restrict normalization to the touched node, or format
      only inserted syntax.
- [ ] **`[found]` `AddProperty` doesn't validate its CLR type.**
      `DiagramEditor.AddProperty` (`DiagramEditor.cs:186`) passes `clrType`
      straight into a synthesized property with no check, unlike
      `ChangePropertyType` which guards with `IsValidTypeToken`. Only reachable
      from the UI's fixed dropdown today, so low severity — but it's a trap for
      the next caller.
- [ ] **`[found]` No confirmation on destructive gestures.** Removing an entity
      or property rewrites the source immediately. Undo exists, but a
      confirmation on entity removal is cheap insurance for a no-code user.
- [ ] **`[found]` Upload size ceiling is 20 MB.** `Home.razor:259` caps
      `OpenReadStream`. A real repo zip (especially one including `bin`/`obj`)
      exceeds this and currently surfaces as the generic "something went wrong"
      error. Once F4's path filter exists, most of the bulk is skippable anyway —
      but the failure should say what actually happened.

## Priority 5 — Docs

- [ ] **`[found]` README overstates round-trip support.** "Upload your existing
      entity classes … and download regenerated C# source" is true only for a
      single-file paste today; a multi-file zip loses everything but two files
      (F2/F3). State the current limits plainly until F2/F3 land.
- [ ] **`[found]` "Unsupported EF Core features" list is out of date.** It still
      says "no diagnostic fires for any of them", which the Round 3
      `UnrecognizedConfigCall` work fixed for entity-scoped calls — but it's
      still true for model-level calls and lambda-argument bodies (W4). Rewrite
      the section against `FluentConfigParser.RecognizedCallNames`
      (`FluentConfigParser.cs:16-26`), which is the authoritative list, and
      separate "flagged" from "silently dropped".
