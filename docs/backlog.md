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

- [ ] **`[carried]/[verified]` F3 — The regenerated `Entities.cs` does not
      compile.**
      `ProjectArchiveReader.cs:81-82` joins every class file with a blank line
      into one blob. Two entity files each carrying their own `using` block and
      file-scoped namespace concatenate into a single illegal compilation unit:

      ```
      concatenated multi-file class source: 1 syntax error
          CS1529 A using clause must precede all other elements
                 defined in the namespace ... @ line 9
      ```

      Multiple `namespace X;` declarations in one file are also illegal, and all
      types silently collapse into whichever namespace lands first — changing
      every type's fully-qualified name. Any project with more than one entity
      file produces broken output.

      The real fix is **per-file round-trip**: keep each uploaded file as its
      own unit (parse per file, rewrite the file that owns the entity via the
      already-captured origins — DiagramEditor would need to track per-file
      state and route cross-file-aware edits like entity rename to every file
      that references the renamed type), re-emit passthrough files verbatim,
      and preserve the original paths. F2's 2026-07-23 fix covers the
      single-class-file/single-config-file case and passthrough files; this
      item is what's left for genuinely multi-file class/config projects.

- [ ] **`[found]/[verified]` F4 — Migrations, `ModelSnapshot`, and `obj/` are
      parsed as entities.**
      `ProjectArchiveReader.Read` (`ProjectArchiveReader.cs:34-51`) iterates
      every zip entry with no path filtering. From the test zip above the diagram
      rendered `Entities: Init, Customer, Order` — `Init` being the migration
      class. A real repo zip also carries `AppDbContextModelSnapshot.cs`,
      `obj/Debug/**/*.AssemblyInfo.cs`, `*.g.cs`, and test-project classes; all
      become tables in the diagram and all get folded into the downloaded
      `Entities.cs`. Add a path filter (`bin/`, `obj/`, `Migrations/`,
      `*ModelSnapshot.cs`, `*.g.cs`, `*.Designer.cs`) and surface what was
      skipped as a diagnostic rather than silently.

- [ ] **`[found]/[verified]` F5 — Renaming to a C# keyword corrupts the source.**
      `DiagramEditor.cs:51` guards with `SyntaxFacts.IsValidIdentifier`, which
      validates lexical identifier *shape* and therefore accepts reserved words.
      Renaming `Blog` → `class` produces:

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
      `RenameProperty` (`DiagramEditor.cs:94`) has the same gap and additionally
      permits a property named identically to its enclosing type (CS0542). Fix:
      reject `SyntaxFacts.GetKeywordKind(name) != None` (and contextual keywords
      where they'd break), and reject a property name equal to its entity name.

- [ ] **`[found]/[verified]` F6 — The "Default value" field emits raw unquoted
      text.**
      `DiagramEditor.SetDefaultValue` (`DiagramEditor.cs:857`) validates only that
      the text parses as *some* C# expression, so what a DBA would naturally type
      produces non-compiling source:

      ```csharp
      entity.Property(e => e.Title).HasDefaultValue(GETDATE())   // won't compile
      entity.Property(e => e.Title).HasDefaultValue(Unknown)     // won't compile
      ```

      They must know to type `"Unknown"` with C# quotes, and nothing in the UI
      says so. Fix: interpret the field by the property's CLR type (quote for
      string/Guid/DateTime, pass through for numeric/bool), and add a separate
      "Default value SQL" field wired to `HasDefaultValueSql` (see P2) — which is
      the call the SQL-shaped input actually wants.

## Priority 1 — Models that render *wrong*, with no warning

> Worse than a missing feature: the DBA has no way to tell the diagram is lying.

- [ ] **`[found]/[verified]` W1 — No EF conventions are applied.**
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

- [ ] **`[found]/[verified]` W2 — Inheritance renders as unrelated fragments.**
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

- [ ] **`[found]/[verified]` W3 — Owned types render as their own tables.**
      `OwnsOne(e => e.ShippingAddress, ...)` is flagged unrecognized, but the
      owned `Address` class is still parsed from the class file and drawn as a
      standalone table with its own columns. The DBA sees a table that does not
      exist in the database. At minimum, suppress the standalone node and show
      the owned columns inline on the owner with a diagnostic; full `OwnsOne`/
      `OwnsMany` parsing is the real fix (see P2).

- [ ] **`[found]/[verified]` W4 — Model-level config is invisible, with no
      diagnostic.**
      `FluentConfigParser.ParseUnrecognizedCalls` only walks calls *inside* an
      `Entity<T>()` scope (`FluentSyntaxHelpers.FindConfigurationScopes`), so
      anything hung off `modelBuilder` directly is neither parsed nor flagged.
      Verified silently dropped:

      - `modelBuilder.HasDefaultSchema("sales")` — every table's schema is wrong
        in the diagram, no warning
      - `modelBuilder.HasSequence<int>(...)`
      - `modelBuilder.ApplyConfigurationsFromAssembly(...)`
      - `ToTable(t => t.HasCheckConstraint(...))` — argument lambda bodies aren't
        walked either, so check constraints vanish without a diagnostic

      Fix: extend the unrecognized-call scan to model-level calls and to
      recognized calls' lambda arguments, then parse `HasDefaultSchema` (cheap,
      and it corrects every entity's rendered schema).

- [ ] **`[found]` W5 — No EF-validity diagnostics at all.**
      All 33 codes in `DiagnosticCodes.cs` mean "I couldn't read this syntax".
      Nothing means "your model is invalid". A no-code user needs the second
      kind: entity with no key (and not `HasNoKey`), FK targeting a non-key/
      non-alternate-key, duplicate column names on one table, entity with no
      `DbSet` and no `Entity<T>()` registration, `IsRequired()` on a nullable CLR
      property, precision/scale on a non-decimal, index over a property that no
      longer exists. Surface these as a separate "model problems" panel, distinct
      from parse diagnostics.

## Priority 2 — EF surface not parsed at all

> All of these are correctly *preserved* in the source through edits (the
> rewriter is surgical), and all now fire `UnrecognizedConfigCall` — but none is
> shown in the diagram or editable. Ordered by how much a **database designer**
> would miss it.

- [ ] **`[found]` SQL-shaped mapping the DBA will look for first:**
      `HasDefaultValueSql`, `HasComputedColumnSql`, `HasCheckConstraint`,
      `HasConstraintName` (FK name), `HasName` / `HasDatabaseName` (PK and index
      names), `HasSequence` / `UseSequence`, `HasDefaultSchema`. These are the
      most conspicuous holes for the target user — they're all things visible in
      SSMS that the diagram simply doesn't have.
- [ ] **`[found]` Owned & complex types:** `OwnsOne`, `OwnsMany`,
      `ComplexProperty`. See W3 — currently actively misleading, not just absent.
- [ ] **`[found]` Inheritance:** `HasDiscriminator` / `HasValue`, TPT
      (`UseTptMappingStrategy`), TPC. See W2.
- [ ] **`[found]` Value converters and enums:** `HasConversion` (all overloads),
      `HasConversion<string>()` on enum properties. Enum properties currently
      render as their bare CLR type with no indication of how they're stored.
- [ ] **`[found]` `HasPrincipalKey`.** Already noted as unsupported in the README;
      relevant now that alternate keys are parsed, since a relationship can
      legitimately target one.
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
