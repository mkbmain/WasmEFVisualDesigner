# Data-annotation parsing & P0 trust diagnostics — Design

**Status:** approved, ready for planning
**Backlog item:** Round 2 review, Priority 0 — "Silent data loss on real-world input"
(all three items: data-annotation configuration unread, duplicate entity names collide
silently, nested type declarations dropped without a diagnostic)

## Problem

`EntityClassParser` only reads CLR shape (property names/types/nullability) and uses
attributes solely to *exclude* `[NotMapped]` properties. Three related trust gaps follow
from this, all discovered in the 2026-07-16 backlog review:

1. **Data annotations are invisible.** `[Key]`, `[Required]`, `[MaxLength]`/
   `[StringLength]`, `[Column]`, `[Table]`, `[Precision]`, `[ForeignKey]` etc. are never
   read. A large share of real EF projects configure their model this way instead of (or
   alongside) the fluent API, so those models render with missing keys, missing
   constraints, wrong nullability, and missing relationships — with no diagnostic. This
   is the single biggest "your real project renders wrong" gap.
2. **Duplicate entity names collide silently.** `DiagramEditor` keys entities by bare
   `Name` in a `Dictionary<string, Guid>`, and `DiagramSync.Rebuild` indexes
   `entityIds[entity.Name]`. Two classes with the same short name overwrite each other.
3. **Nested type declarations are dropped without a diagnostic.**
   `EntityClassParser.Parse` filters to top-level types only; a nested entity class is
   silently invisible.

## Scope

- Data annotations parse **into the model**, not just a diagnostic — annotation-only
  projects should render as correctly as fluent-configured ones.
- Annotation set covered: `[Key]` (incl. composite via `[Column(Order=)]`), `[Required]`,
  `[MaxLength]`/`[StringLength]`, `[Column]`, `[Table]`, `[Precision]`, and
  `[ForeignKey]` (relationship inference).
- `[DatabaseGenerated]` is explicitly **out of scope** — it has no existing model field
  to map onto (unlike the others), and the fluent side has the same gap
  (`ValueGeneratedNever`/`OnAdd`/`OnUpdate` aren't parsed from fluent either). Deferred to
  a follow-up that addresses both sides together.
- Duplicate names: diagnostic + keep-first-drop-rest (matches today's de-facto collision
  behavior everywhere downstream, just makes it visible — no changes to `DiagramEditor`/
  `DiagramSync` identity logic).
- Nested types: diagnostic only, no parsing. Nested EF entities are rare; not worth the
  added identity/collision complexity right now.

## Why annotations slot in without new `Merging` types (mostly)

Fluent config and CLR/annotation data come from **different source blobs** in this app's
model (`classSource` vs. `configSource`), which is why fluent config needs its own
`*Config` DTOs and `ModelMerger.Apply*` merge step — it's parsed separately and folded in
afterward. Annotations, by contrast, live on the *same* syntax tree `EntityClassParser`
already walks for CLR shape. So the six scalar annotations fold directly into
`EntityModel`/`PropertyModel` during the existing `ParseEntity`/`ParseProperty` pass — no
new config DTOs, no new merge step.

Precedence falls out for free: `DiagramModelBuilder.Build` already runs
`ModelMerger.Apply*` *after* `EntityClassParser.Parse`. Every `Apply*` method only
overwrites a field when a matching fluent config exists for that entity/property;
otherwise the value already on the `EntityModel` (now potentially annotation-derived)
passes through untouched. So fluent API continues to win on conflict — matching real EF
Core precedence (Fluent API > Data Annotations > conventions) — with **zero changes to
`ModelMerger`**.

`[ForeignKey]` is the exception: relationships aren't a per-property field, they require
cross-referencing all parsed entities (the same problem `FluentConfigParser.
ParseRelationships` already solves). This one gets a second pass, described below.

## `EntityClassParser` changes

### Attribute reading helpers

New private helpers alongside the existing `HasNotMappedAttribute`:

```csharp
private static AttributeSyntax? FindAttribute(SyntaxList<AttributeListSyntax> lists, string name)
```

Matches both `Foo` and `FooAttribute` spelling (same pattern as
`HasNotMappedAttribute`), returns the first match or null. Used for `[Key]`,
`[Required]`, `[MaxLength]`, `[StringLength]`, `[Column]`, `[Table]`, `[Precision]`,
`[ForeignKey]`.

Argument extraction (positional int/string, named `Name=`/`TypeName=`/`Schema=`/`Order=`)
reuses the existing literal-reading approach `FluentConfigParser` already has for fluent
arguments (`int.TryParse(arg.ToString())` etc.) — same trade-off, same existing
diagnostic channel: a non-literal annotation argument (e.g. `[MaxLength(MaxNameLength)]`)
is skipped and does **not** throw, consistent with the equivalent fluent gap already
documented in the backlog (Priority 0, "Non-literal `HasMaxLength` arguments").
No new diagnostic code for this sub-case — it's the same class of limitation already
accepted for fluent, not new behavior to call out separately.

### `ParseProperty` / `ParseEntity` changes

`ParseProperty` gains reads for `[Required]`, `[MaxLength]`/`[StringLength]`, `[Column]`,
`[Precision]`, setting `IsRequiredOverride`, `MaxLength`, `ColumnName`/`ColumnType`,
`Precision`/`Scale` on the returned `PropertyModel` (all already-existing fields — this
is purely a second way to populate them).

`ParseEntity` gains:
- Class-level `[Table(Name[, Schema=])]` read into `EntityModel.TableName`/`Schema`.
- Key collection: scan all body properties (not positional — see Out of scope) for
  `[Key]`. Zero matches → `KeyPropertyNames` stays empty (unchanged). One or more matches
  → order them by `[Column(Order=n)]` when present (ascending; properties without an
  explicit `Order` sort after those with one, in declaration order), else declaration
  order, and set `EntityModel.KeyPropertyNames`.

`[StringLength]` and `[MaxLength]` both map to `PropertyModel.MaxLength`; if a property
somehow carries both, `[MaxLength]` wins (checked first) since it's EF's more specific
attribute for this purpose.

### Relationships: `EntityClassParser.ParseRelationships`

New method, mirroring `FluentConfigParser.ParseRelationships`'s signature:

```csharp
public ParseResult<IReadOnlyList<RelationshipConfig>> ParseRelationships(
    string sourceCode, IReadOnlyList<EntityModel> entities)
```

For each parsed type, find `[ForeignKey("X")]` wherever it appears — on a scalar FK
property (naming its nav property) or on a reference nav property (naming its FK
property) — resolve the paired property `X` on the same type, then:

1. Identify which of the pair is the navigation: the one whose CLR type (unwrapped of
   `?`) matches another entry in `entities` by name. The type carrying the FK scalar
   property is the **dependent**; the referenced entity is the **principal**.
2. Determine `Kind` by inspecting the principal entity's properties for a back-reference
   to the dependent:
   - a collection-typed property (`ICollection<T>`, `List<T>`, `IEnumerable<T>`,
     `IReadOnlyList<T>`, `HashSet<T>` — same type-shape check
     `FluentSyntaxHelpers`/`FluentConfigParser` already uses for collection navs) whose
     element type is the dependent → `OneToMany`, `PrincipalNavigation` = that property's
     name.
   - else a scalar reference property whose type is the dependent → `OneToOne`.
   - else → `OneToMany` with `PrincipalNavigation = null` (unidirectional FK is the
     common real-world shape; `OneToMany` is the correct default absent contrary
     evidence, matching a plain `int BlogId` + `Blog Blog` pair with no collection back on
     `Blog`).
3. `DependentNavigation` = the nav property name on the dependent side (from step 1).
4. `ForeignKeyProperties` = `[X]` — single-column only; composite FKs via annotations
   aren't a real EF Core shape.
5. If the attribute names a property that doesn't exist on the type, or neither half of
   the pair resolves to a type matching a known entity, skip silently (this is
   indistinguishable from "not actually an EF relationship" — e.g. a `[ForeignKey]` typo
   or a non-entity reference type — and the fluent parser has the same silent-skip
   behavior for unresolvable navs today).

`DiagramModelBuilder.Build` calls this alongside the existing fluent
`ParseRelationships`, unions the two `RelationshipConfig` lists, and dedupes: an
annotation-derived relationship is dropped if a fluent one already exists with the same
`(PrincipalEntity, DependentEntity, ForeignKeyProperties)` — fluent wins on conflict,
consistent with every other config kind.

## Duplicate entity names

In `EntityClassParser.Parse`, after building `entities` from `typeDeclarations`:

```csharp
var duplicateGroups = entities
    .GroupBy(e => e.Name)
    .Where(g => g.Count() > 1);
```

For each group, emit a `DuplicateEntityName` diagnostic (new `DiagnosticCodes` entry)
naming the type and how many declarations collided. Return only the first entity per
name (`entities.GroupBy(...).Select(g => g.First())`, preserving original declaration
order via `DistinctBy`-equivalent — first-in-file wins). No changes to `DiagramEditor` or
`DiagramSync` — they already behave as "last one wins" today for a duplicate key insert
followed by a rebuild pass keyed the same way; feeding them an already-deduplicated list
makes that consistent as "first one wins" with a visible diagnostic instead of a silent
overwrite.

## Nested type declarations

`EntityClassParser.Parse` already computes top-level `typeDeclarations`. Add a sibling
query for the dropped set:

```csharp
var nestedTypeDeclarations = root.DescendantNodes()
    .OfType<TypeDeclarationSyntax>()
    .Where(t => t is ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax)
    .Where(t => t.Ancestors().OfType<TypeDeclarationSyntax>().Any());
```

Emit one `NestedTypeDeclaration` diagnostic per entry, naming the nested type and its
immediate enclosing type, regardless of whether it "looks like" an entity — syntax alone
can't distinguish an entity from a nested DTO/helper, so a diagnostic that sometimes
fires on a non-entity nested type is an acceptable false positive; silence on a real one
is not.

## `DiagnosticCodes` additions

Three new constants: `DuplicateEntityName`, `NestedTypeDeclaration`, plus reuse of
existing codes where the sub-case already matches (non-literal annotation args use the
existing `UnresolvablePropertyName`-style pattern only where a name truly can't be
resolved — most non-literal-arg cases are a silent skip, not a diagnostic, matching the
fluent precedent noted above).

## Testing

**`EntityClassParserTests`** (new cases):
- `[Key]` single property → `KeyPropertyNames`
- `[Key]` on multiple properties, ordered via `[Column(Order=)]`
- `[Key]` on multiple properties with no `Order` → declaration order
- `[Required]` → `IsRequiredOverride = true`
- `[MaxLength(n)]` and `[StringLength(n)]` → `MaxLength`; both present → `[MaxLength]`
  wins
- `[Column(Name=, TypeName=)]` → `ColumnName`/`ColumnType`
- `[Table("Name", Schema="dbo")]` (class-level) → `TableName`/`Schema`
- `[Precision(18, 2)]` and `[Precision(18)]` (no scale) → `Precision`/`Scale`
- Non-literal annotation argument (e.g. `[MaxLength(MaxNameLength)]`) is skipped, not
  thrown
- Duplicate entity names → diagnostic + first-wins, verified against a fixture with two
  same-named classes
- Nested class/record/struct → diagnostic, type excluded from the returned entities
- A file mixing annotations and no fluent config at all still produces a fully-populated
  `EntityModel` (the "annotation-only project" end-to-end case this whole item exists
  for)

**New `EntityClassParser.ParseRelationships` tests** (own suite or appended to
`EntityClassParserTests`):
- `[ForeignKey("BlogId")]` on the nav property
- `[ForeignKey("Blog")]` on the scalar FK property
- Principal has a collection back-reference → `OneToMany` with `PrincipalNavigation` set
- Principal has a scalar back-reference → `OneToOne`
- No back-reference on principal → `OneToMany`, `PrincipalNavigation = null`
- Attribute names a nonexistent property → skipped, no throw
- Neither side of the pair resolves to a known entity → skipped, no throw

**`DiagramModelBuilder`-level test** (in `EfSchemaVisualizer.Web.Tests` or wherever its
existing coverage lives, if any — otherwise a new small integration test alongside the
`Core` tests calling `DiagramModelBuilder.Build` directly):
- Fluent `HasOne`/`WithMany` and an annotation `[ForeignKey]` both present for the same
  FK → only the fluent one survives in the merged relationship list (precedence test)
- A fluent `HasMaxLength` and an annotation `[MaxLength]` on the same property with
  different values → fluent value wins (precedence test for the free-merge claim)

## Out of scope (this pass)

- `[DatabaseGenerated]` — no model field exists for it yet; deferred alongside the
  equivalent fluent gap (`ValueGeneratedNever`/`OnAdd`/`OnUpdate` also unparsed).
- `[InverseProperty]`, many-to-many via annotations (not a real EF Core shape).
- `[Key]`/other annotations on record positional parameters — positional properties are
  already a separate, more limited code path (`ParseParameterProperty`); extending
  annotation support there is folded into the existing "record positional parameters
  render but aren't editable" backlog item (Round 2, Priority 1), not this one.
- Rewriter/write-back for any annotation-derived field — annotations are a *read* path
  only, same as the existing fluent-parse-only precedent for relationships. Editing an
  annotation-configured property through the diagram already writes back via the fluent
  rewriter today (`OnModelCreatingRewriter`) since the rewriter operates on
  `configSource`, independent of how the value was originally read; this design doesn't
  change that.
- Disambiguating duplicate entity names beyond keep-first-drop-rest (e.g. namespace
  qualification) — deferred; today's silent behavior is already "one wins," this pass
  only adds visibility.
- Parsing nested type declarations as entities — diagnostic only.
