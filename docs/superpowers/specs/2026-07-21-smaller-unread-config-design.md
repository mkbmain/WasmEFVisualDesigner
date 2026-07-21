# Smaller unread config — design

> Addresses the Round 3 Priority 3 backlog item in `docs/backlog.md`:
> "Smaller unread config." Covers `HasQueryFilter`, `HasComment`,
> `IsUnicode`/`IsFixedLength`, `UseCollation`, `ToJson`, temporal tables
> (`ToTable(b => b.IsTemporal())`), `SplitToTable`, and `[InverseProperty]`.
>
> `[DeleteBehavior]` — also named in the original backlog bullet — was dropped
> during scoping: it isn't a real EF Core data annotation (delete behavior is
> fluent-only via `.OnDelete(DeleteBehavior.X)`, already parsed by
> `SetRelationship`). Treated as an inaccuracy in the original review pass.

## Goal

Eight distinct, individually niche EF Core constructs are unread today. All
of them already trip the generic `UnrecognizedConfigCall` diagnostic (or, for
the two attributes, are silently ignored — `EntityClassParser` doesn't scan
for them at all), so nothing is *silently* dropped, but nothing is visible in
the model either. This pass parses and merges all eight into
`EntityModel`/`PropertyModel`, following the parse → DTO → `ModelMerger.Apply*`
pattern established by every prior config kind. **Parse + merge only** — no
rewriter, no `DiagramEditor` methods, no diagram UI — matching the precedent
set by relationships and value-generation: these don't have diagram
real-estate demand yet, and eight at once is enough surface area for one
slice without also inventing eight new UI affordances.

## Model

`PropertyModel` (`src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`) gains
five new optional-positional fields, appended after `IsConcurrencyToken`:

- `string? Comment = null`
- `bool? IsUnicode = null`
- `bool? IsFixedLength = null`
- `string? Collation = null`
- `string? InverseProperty = null`

`EntityModel` (`src/EfSchemaVisualizer.Core/Model/EntityModel.cs`) gains six
new optional-positional fields:

- `string? Comment = null`
- `bool HasQueryFilter = false`
- `bool IsJson = false`
- `string? JsonColumnName = null`
- `bool IsTemporal = false`
- `IReadOnlyList<string>? SplitTables = null` (defaulted to an empty list via
  the same `init`-with-null-coalesce pattern `KeyPropertyNames`/`Indexes`/
  `AlternateKeys` already use)

`IsUnicode`/`IsFixedLength` are `bool?` (not `bool`) because EF itself treats
"unset" as distinct from `false` (`IsUnicode(false)` forces ASCII;
unset means "let the provider decide") — the same reasoning `IsRequiredOverride`
already follows for the same three-state shape.

## Parsing — fluent side

New DTOs in `src/EfSchemaVisualizer.Core/Merging/`:

```csharp
public sealed record QueryFilterConfig(string EntityName);
public sealed record EntityCommentConfig(string EntityName, string Comment);
public sealed record PropertyCommentConfig(string EntityName, string PropertyName, string Comment);
public sealed record UnicodeConfig(string EntityName, string PropertyName, bool IsUnicode);
public sealed record FixedLengthConfig(string EntityName, string PropertyName, bool IsFixedLength);
public sealed record CollationConfig(string EntityName, string PropertyName, string Collation);
public sealed record JsonConfig(string EntityName, string? ColumnName);
public sealed record TemporalConfig(string EntityName);
public sealed record SplitToTableConfig(string EntityName, string TableName);
```

New `FluentConfigParser` methods, one per call, each walking
`FluentSyntaxHelpers.FindConfigurationScopes` scopes as usual:

- **`ParseQueryFilters`** → `IReadOnlyList<QueryFilterConfig>` (no
  `ParseResult` wrapper: the predicate expression can't be meaningfully read
  or fail to read — presence is the only signal, same reasoning as
  `ParseKeylessEntities`). `FindCallsNamed(scope, "HasQueryFilter")` →
  one `QueryFilterConfig(entityName)` per match (deduped like
  `ParseKeylessEntities`).

- **`ParseComments`** → `ParseResult<IReadOnlyList<EntityCommentConfig>>` +
  a second `ParseResult<IReadOnlyList<PropertyCommentConfig>>` (two return
  values via a tuple, since `HasComment` is legal at both entity and property
  scope and the two need different DTOs — simplest to keep them as two
  independent result lists rather than a union type). For each
  `FindCallsNamed(scope, "HasComment")` call: try `GetPropertyNameFor` first;
  if it resolves, read the string-literal arg into a `PropertyCommentConfig`
  (unreadable → new `UnreadableHasCommentArgument` diagnostic, keyed to the
  property); if it doesn't resolve (call is chained directly off the entity
  receiver, not a `.Property(...)` chain), treat it as entity-level and read
  into an `EntityCommentConfig` the same way `ParseSqlQueries` reads
  `ToSqlQuery`. This mirrors how `GetPropertyNameFor` returning `null` is
  already the "not a property-scoped call" signal used implicitly elsewhere
  (e.g. bare `Entity<T>()`-chained calls).

- **`ParseUnicodeFlags`** / **`ParseFixedLengthFlags`** →
  `ParseResult<IReadOnlyList<UnicodeConfig>>` /
  `ParseResult<IReadOnlyList<FixedLengthConfig>>`. Identical shape to
  `ParseIsRequired`: resolve property via `GetPropertyNameFor`
  (`UnresolvablePropertyName` if it fails), no-arg call → `true`, boolean
  literal arg → that value, anything else →
  `UnreadableIsUnicodeArgument`/`UnreadableIsFixedLengthArgument`.

- **`ParseCollations`** → `ParseResult<IReadOnlyList<CollationConfig>>`.
  Identical shape to `ParseColumnType`: resolve property, read a string
  literal arg, `UnreadableUseCollationArgument` on failure.

- **`ParseJsonMappings`** → `ParseResult<IReadOnlyList<JsonConfig>>`.
  `FindCallsNamed(scope, "ToJson")`: 0 args → `JsonConfig(entityName, null)`;
  1 string-literal arg → `JsonConfig(entityName, literal)`; anything else →
  `UnreadableToJsonArgument`.

- **Temporal tables extend the existing `ParseTableMappings`**, rather than
  adding a new method, because both read the same `ToTable` call name and
  today's literal-only branch actively misfires on the lambda-config
  overload (`arguments[0].Expression is not LiteralExpressionSyntax` is
  `true` for a lambda, so it currently falls into the "table name not a
  string literal" diagnostic branch — wrong code, wrong message, and the
  `IsTemporal()` signal is lost entirely). New logic before the existing
  literal check: if `arguments[0].Expression` is a lambda
  (`SimpleLambdaExpressionSyntax` or `ParenthesizedLambdaExpressionSyntax`),
  this is the single-arg config-only overload — walk the lambda body for a
  call named `IsTemporal` via `FluentSyntaxHelpers.FindCallsNamed`-style
  matching and, if found, add a `TemporalConfig(entityName)` to a new result
  list; otherwise fall through unchanged (a config lambda that doesn't call
  `IsTemporal()` configures something else we don't read — no diagnostic,
  matching `SplitToTable`'s builder-lambda scope cut below). If
  `arguments[1].Expression` is a lambda instead of a literal (the
  `ToTable("Name", b => b.IsTemporal())` two-arg overload), same
  `IsTemporal()` scan applies to `arguments[1]` instead of falling into the
  existing "schema argument not a string literal" diagnostic branch.
  `ParseTableMappings`'s signature changes to
  `ParseResult<(IReadOnlyList<TableConfig> Tables, IReadOnlyList<TemporalConfig> Temporal)>`
  — the two are parsed in the same pass since they share the same call site,
  avoiding a second full walk of every `ToTable` call.

- **`ParseSplitTables`** → `ParseResult<IReadOnlyList<SplitToTableConfig>>`.
  `FindCallsNamed(scope, "SplitToTable")`: first arg must be a string
  literal (table name) → `SplitToTableConfig(entityName, tableName)`;
  missing/non-literal first arg → `UnreadableSplitToTableArgument`. The
  builder lambda (second-to-last arg) and optional schema arg are not read —
  same scope cut as `UsingEntity`'s join-config internals. A `SplitToTable`
  call chained onto another `SplitToTable` call's scope (splitting across
  three or more tables) is walked as a normal chained call by
  `FindConfigChainCalls`, so multiple `SplitToTableConfig`s per entity are
  expected and both feed `EntityModel.SplitTables`.

`RecognizedCallNames` gains `"HasQueryFilter"`, `"HasComment"`, `"IsUnicode"`,
`"IsFixedLength"`, `"UseCollation"`, `"ToJson"`, `"SplitToTable"` (`"ToTable"`
is already present).

## Parsing — attribute side

`EntityClassParser.ParseProperty` gets one new read, same shape as the
existing `[ForeignKey]` handling but simpler (no relationship resolution,
just a value carry-through):

```csharp
string? inverseProperty = FindAttribute(attributeLists, "InverseProperty") is { } attr
    ? GetAttributeStringArgument(attr) // reuses whatever literal-arg helper [Column]/[Table] already use
    : null;
```

threaded into the `PropertyModel` constructor as the initial (and, since
there's no fluent equivalent to `[InverseProperty]`, final) value of
`InverseProperty`. This is metadata only — it is **not** wired into
`TryResolveForeignKeyRelationship` or any relationship-pairing logic. Using
it to disambiguate multi-navigation-property pairings is a real
correctness improvement but a separate, riskier change (touches relationship
resolution, which several other backlog items have already needed careful,
isolated passes for); out of scope here.

## Merging

`ModelMerger` gains one `Apply*` method per config kind, added to
`DiagramModelBuilder.Build` alongside the existing calls:

- `ApplyQueryFilters(EntityModel, IReadOnlyList<QueryFilterConfig>)` — sets
  `HasQueryFilter = true` if the entity's name appears in the list.
- `ApplyEntityComments(EntityModel, IReadOnlyList<EntityCommentConfig>)` /
  `ApplyPropertyComments(EntityModel, IReadOnlyList<PropertyCommentConfig>)`
  — straightforward `FirstOrDefault`/`IndexByProperty` lookups, same shape as
  `ApplyColumnNames`.
- `ApplyUnicodeFlags` / `ApplyFixedLengthFlags` / `ApplyCollations` — same
  `IndexByProperty` shape as `ApplyColumnTypes`.
- `ApplyJsonMappings(EntityModel, IReadOnlyList<JsonConfig>)` — sets `IsJson`
  and `JsonColumnName`.
- `ApplyTemporal(EntityModel, IReadOnlyList<TemporalConfig>)` — sets
  `IsTemporal = true` if present.
- `ApplySplitTables(EntityModel, IReadOnlyList<SplitToTableConfig>)` —
  projects all matching configs' table names into `EntityModel.SplitTables`
  (full-replace per entity, matching `ApplyAlternateKeys`'s precedent for a
  list-shaped field).

No fluent-vs-attribute precedence question for any of these: `InverseProperty`
has no fluent counterpart, and none of the other seven have an attribute
counterpart, so there's nothing to arbitrate.

## Testing

Same shape as every prior parse-only pass (relationships, ignore/shadow
properties):

- `FluentConfigParserTests`: one test class per new `Parse*` method —
  happy path, unreadable-argument diagnostic, unresolvable-property-name
  diagnostic (property-scoped ones only). `ParseComments` gets explicit
  entity-vs-property disambiguation tests. `ParseTableMappings` gets new
  cases for both `ToTable(b => b.IsTemporal())` and
  `ToTable("Name", b => b.IsTemporal())`, plus a regression test that a
  config lambda *not* calling `IsTemporal()` no longer emits the old
  (incorrect) "not a string literal" diagnostic.
- `EntityClassParserTests`: `[InverseProperty("Nav")]` detection, and
  absence (property with no such attribute → `null`, not empty string).
- `ModelMergerTests`: one test per new `Apply*` method.
- Extend `RoundTripFuzzTests`' corpus with one example each of
  `HasQueryFilter`, `SplitToTable`, and a temporal `ToTable(b => ...)` call,
  confirming: they no longer appear as `UnrecognizedConfigCall` diagnostics
  (except `SplitToTable`'s builder-lambda internals, which still aren't
  modeled and so still fall outside what the fuzz test's "preserved
  verbatim" check covers — same category as `UsingEntity`'s existing
  carve-out), and that renaming an unrelated property elsewhere in the file
  leaves all eight new fields on other entities untouched.

## Explicitly out of scope

- Rewriter (`OnModelCreatingRewriter`), `DiagramEditor` methods, and diagram
  UI for all eight — parse/merge only, per the scoping decision above.
- `SplitToTable`'s per-property-to-table assignment (the builder lambda body)
  — only secondary table names are captured.
- `HasQueryFilter`'s predicate expression — presence only, not the filter
  logic itself.
- Wiring `[InverseProperty]` into relationship pairing/resolution.
- `UseCollation` at the database/model level (`modelBuilder.UseCollation(...)`,
  a different overload than the per-property one covered here) — not
  mentioned in the backlog item and a different scope (model-wide default,
  not a per-entity/property fact).
- Validating any of the eight against contradictory combinations (e.g.
  `ToJson()` on a keyless entity, `IsUnicode(true)` on a non-string
  property) — matches this codebase's consistent precedent of not
  validating EF's own invariants.
