# `HasIndex` Fluent Config — Design

**Status:** approved, ready for planning
**Backlog item:** Priority 2 — `[spec] Indexes — HasIndex, including unique`

## Problem

Nothing today models EF Core indexes. `entity.HasIndex(...)` calls — single-property,
composite, uniqueness flag, and named-index overload — are invisible to the parser,
merger, and rewriter. This item adds full round-trip support, following the same
parse → merge → rewrite pattern established for `HasMaxLength`, `IsRequired`, and
`HasKey`, with three index-specific differences from `HasKey`:

1. **An entity may have many indexes** — `Indexes` is a list, not a single optional value.
2. **`.IsUnique()` is chained onto `HasIndex(...)`** — a chained modifier on the call
   itself rather than a sibling call (like `HasKey`) or a property-attribute (like
   `IsRequired`).
3. **Rewrite identity is the property-set** — since a single entity may have multiple
   indexes, the rewriter locates the index to mutate or remove by matching its ordered
   column set.

## Model changes

New `IndexModel` record:

```csharp
// src/EfSchemaVisualizer.Core/Model/IndexModel.cs
public sealed record IndexModel(
    IReadOnlyList<string> PropertyNames,
    bool IsUnique,
    string? Name = null);
```

`EntityModel` gains one new field:

```csharp
public sealed record EntityModel(
    string Name,
    IReadOnlyList<PropertyModel> Properties,
    IReadOnlyList<string>? KeyPropertyNames = null,
    IReadOnlyList<IndexModel>? Indexes = null)
{
    public IReadOnlyList<string> KeyPropertyNames { get; init; } = KeyPropertyNames ?? new List<string>();
    public IReadOnlyList<IndexModel> Indexes { get; init; } = Indexes ?? new List<IndexModel>();
}
```

- `Indexes` is empty (not null) when no `HasIndex(...)` calls configure the entity.
- Order within `PropertyNames` is significant for composite indexes and is preserved.
- `PropertyModel` is untouched — index membership is an entity-level concept, the same
  reasoning that keeps composite-key order representable via `KeyPropertyNames` rather
  than a per-property flag.
- Existing `EntityClassParser` call sites construct `EntityModel` without `Indexes`;
  it defaults to empty and is only populated by `ModelMerger`.

## New DTO: `IndexConfig`

```csharp
// src/EfSchemaVisualizer.Core/Parsing/IndexConfig.cs
public sealed record IndexConfig(
    string EntityName,
    IReadOnlyList<string> PropertyNames,
    bool IsUnique,
    string? Name);
```

## Parsing: `FluentConfigParser.ParseIndexes`

New method, same skeleton as `ParseKeys`:

```csharp
public ParseResult<IReadOnlyList<IndexConfig>> ParseIndexes(string sourceCode)
```

For each entity invocation in the file, call
`FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasIndex")`. For each
`HasIndex` call found, read three things:

### a) Property names + optional index name

Supported argument shapes:

| Shape | Example | Columns | Name |
|---|---|---|---|
| Single-prop lambda | `e => e.Email` | `["Email"]` | null |
| Composite lambda | `e => new { e.A, e.B }` | `["A","B"]` | null |
| Bare string params (single) | `"Email"` | `["Email"]` | null |
| Bare string params (composite) | `"A", "B"` | `["A","B"]` | null |
| Lambda + name | `e => e.Email, "IX_Email"` | `["Email"]` | `"IX_Email"` |
| String array + name | `new[] {"A","B"}, "IX_AB"` | `["A","B"]` | `"IX_AB"` |

Disambiguation rule: if all arguments are bare string literals, treat them as the
params-overload column names (no index name). If the first argument is an array
creation expression (`new[]{...}`) or a lambda and a second argument is a string
literal, treat the second as the index name.

Unreadable shapes (non-literal args, explicit-name anonymous members `new { K = e.A }`,
block-bodied lambdas, etc.) → diagnostic `UnreadableHasIndexArgument` with entity
name, `PropertyName: null`, and the call span. The call is skipped (not silently
discarded — the diagnostic is the signal).

Multiple `HasIndex` calls on the same entity each produce their own `IndexConfig`
(they are additive, not last-wins).

### b) IsUnique

After resolving the `HasIndex(...)` call node, walk *outward* through the call chain
(toward the enclosing statement) looking for a call named `IsUnique`. This is robust
to ordering (`HasIndex(...).IsUnique().HasFilter(...)` and
`HasIndex(...).HasFilter(...).IsUnique()` both work). A new private helper
`TryReadIsUnique(InvocationExpressionSyntax hasIndexCall)` in `FluentConfigParser`
does this walk. (The rewriter never reads `IsUnique` from source — it always writes
the canonical form from its `isUnique` parameter.)

- Bare `.IsUnique()` (no args) → `true`
- `.IsUnique(true)` or `.IsUnique(false)` → the literal value
- Non-bool-literal arg to `IsUnique` → diagnostic `UnreadableIsUniqueArgument`
  (entity name, `PropertyName: null`, span of arg); the index is still recorded
  with `IsUnique = false` (EF's default)
- No `IsUnique` call in the chain → `false`

### c) Property-name reading shared via FluentSyntaxHelpers

The logic to extract column names from a `HasIndex` argument list is extracted into
`FluentSyntaxHelpers.TryReadIndexPropertyNames(InvocationExpressionSyntax hasIndexCall)`
and returns `(IReadOnlyList<string> PropertyNames, string? Name)?`. This is a new
internal helper used by both the parser and the rewriter (so they share one canonical
property-name resolver). The existing private `TryReadKeyPropertyNames` in
`FluentConfigParser` stays as-is — the overlap is minor and not worth collapsing.

## Merging: `ModelMerger.ApplyIndexes`

```csharp
public static EntityModel ApplyIndexes(EntityModel entity, IReadOnlyList<IndexConfig> configs)
```

Unlike `ApplyKeys` (which takes `FirstOrDefault`), this collects **all** configs
matching `entity.Name` and maps each to an `IndexModel`, appending them to
`entity.Indexes`. No match leaves the list empty. Separate composed call — callers
who want all config types call all four Apply methods.

## Rewriting: `OnModelCreatingRewriter`

```csharp
public string SetIndex(
    string sourceCode,
    string entityName,
    IReadOnlyList<string> propertyNames,
    bool isUnique,
    string? name = null)

public string RemoveIndex(
    string sourceCode,
    string entityName,
    IReadOnlyList<string> propertyNames)
```

**Identity = property-set.** Both methods locate the target `HasIndex` call by calling
`FluentSyntaxHelpers.TryReadIndexPropertyNames` on each `HasIndex` call found via
`FindCallsNamed` and comparing `PropertyNames` with `SequenceEqual`.

### SetIndex — three-case dispatch (mirrors SetKey)

1. **Mutate**: a `HasIndex` on that property-set already exists → replace the entire
   enclosing `ExpressionStatement` expression with a freshly-built canonical chain.
   Rebuilding the whole chain (rather than token surgery) cleanly handles toggling
   `.IsUnique()` and setting/clearing the name.
2. **Insert statement** into an existing `Entity<T>(entity => { … })` block that has
   no `HasIndex` on this property-set.
3. **Synthesize** a new `Entity<T>(entity => { … })` block containing just the
   `HasIndex` statement, when the entity has no `OnModelCreating` configuration at all.

### Canonical write form (always)

- Single column → `e => e.X`; composite → `e => new { e.A, e.B }`
- `name != null` → second argument `"IX_..."` (inline overload, not `.HasDatabaseName(...)`)
- `isUnique == true` → chain `.IsUnique()` (no argument — bare form is idiomatic EF)
- `isUnique == false` → omit `.IsUnique()` entirely (non-unique is EF's default)

The lambda parameter for the key arg is always `e` (same convention as `SetKey`).

### RemoveIndex

Finds the `ExpressionStatement` containing the matching `HasIndex` call (the whole
statement, including any chained `.IsUnique()`) and removes it. No bare receiver to
fall back to, unlike `RemoveMaxLength`/`RemoveIsRequired`. No-op (returns `sourceCode`
unchanged) if no matching call is found.

## New diagnostic codes

| Code | When | PropertyName |
|---|---|---|
| `UnreadableHasIndexArgument` | Column args not resolvable | null (entity-level) |
| `UnreadableIsUniqueArgument` | `.IsUnique(...)` arg not a bool literal | null (entity-level) |

## Testing

New test coverage mirroring the existing suites:

**`FluentConfigParserTests`**
- Single-column lambda (`e => e.Email`)
- Composite lambda (`e => new { e.A, e.B }`) — order preserved
- Bare string single (`"Email"`)
- Bare string composite (`"A", "B"`)
- Lambda + name (`e => e.Email, "IX_Email"`)
- Array + name (`new[] {"A","B"}, "IX_AB"`)
- `IsUnique()` present (bare)
- `IsUnique(false)` present
- Non-bool `.IsUnique` arg → diagnostic `UnreadableIsUniqueArgument`
- Explicit-name anonymous member (`new { K = e.A }`) → diagnostic `UnreadableHasIndexArgument`
- Multiple `HasIndex` on same entity — both appear in result
- No `HasIndex` present → empty result, no diagnostics

**`ModelMergerTests`**
- Multiple configs for same entity all appear in `Indexes`
- No configs → empty `Indexes`
- `Properties` and `KeyPropertyNames` untouched by `ApplyIndexes`

**`OnModelCreatingRewriterTests`**
- `SetIndex` mutate — single column, unique=false → unique=true
- `SetIndex` mutate — composite, change name
- `SetIndex` insert-statement — entity block exists, no prior index on those columns
- `SetIndex` synthesize-block — entity has no config at all
- `SetIndex` — with name; without name; toggle isUnique
- `RemoveIndex` — existing call removed (statement including chained `.IsUnique()`)
- `RemoveIndex` — no-op when no matching call

## Out of scope

- `.HasDatabaseName(...)` as a chained call (defer; inline name arg covers the common case)
- `.HasFilter(...)`, included columns, descending/sort order (`IsDescending`)
- Explicit-name anonymous-type members (`new { K = e.A }`) — treated as diagnostic,
  consistent with the `HasKey` precedent
- Two indexes on identical columns distinguished only by name — property-set identity
  cannot disambiguate them; noted as a known limitation
- Data-annotation `[Index]` attribute (fluent-API only, consistent with rest of engine)
- No validation that property names reference properties that exist on the entity
- No UI consumer (Blazor shell is Priority 4)
