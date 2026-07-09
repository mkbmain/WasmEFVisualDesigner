# `HasKey` Fluent Config — Design

**Status:** approved, ready for planning
**Backlog item:** Priority 2 — `[spec] Keys — HasKey, including composite keys`

## Problem

Nothing today models a EF Core entity's primary key. `entity.HasKey(...)` calls
(single-property, composite, and their string-based overloads) are invisible to the
parser, merger, and rewriter. This item adds full round-trip support, following the
same parse → merge → rewrite pattern already established for `HasMaxLength` and
`IsRequired`, with one structural difference: `HasKey` is entity-level (an ordered set
of property names), not per-property, so it doesn't attach to `PropertyModel`.

## Model changes

`EntityModel` gains one new field:

```csharp
public sealed record EntityModel(
    string Name,
    IReadOnlyList<PropertyModel> Properties,
    IReadOnlyList<string> KeyPropertyNames);
```

- `KeyPropertyNames` is empty when no `HasKey(...)` call configures the entity.
- Order is preserved and significant for composite keys (`["A", "B"]` is a different
  key from `["B", "A"]` semantically, and must round-trip to the same call shape).
- `PropertyModel` is untouched. Key membership isn't duplicated onto individual
  properties — `EntityModel.KeyPropertyNames` is the single source of truth, avoiding
  an ordering-reconstruction problem a per-property `bool IsKey` flag would create.
- Existing `EntityClassParser` call sites pass `KeyPropertyNames: []`; it is only ever
  populated by `ModelMerger`.

## Parsing: `FluentConfigParser.ParseKeys`

New method, same shape as `ParseMaxLengths`/`ParseIsRequired`:

```csharp
public ParseResult<IReadOnlyList<KeyConfig>> ParseKeys(string sourceCode)
```

New DTO:

```csharp
public sealed record KeyConfig(string EntityName, IReadOnlyList<string> PropertyNames);
```

Walk each `Entity<T>` config invocation, find calls named `HasKey` directly on the
entity builder receiver (via the existing `FindCallsNamed(entityInvocation, "HasKey")`
— note `HasKey` is called on `entity` itself, not chained off `Property(...)`, so no
`GetPropertyNameFor` resolution is needed here). For each `HasKey(...)` call found,
read its argument list:

- **Lambda, single property:** `e => e.Id` (`SimpleLambdaExpressionSyntax` with a
  `MemberAccessExpressionSyntax` expression body) → `["Id"]`.
- **Lambda, composite:** `e => new { e.A, e.B }` (`SimpleLambdaExpressionSyntax` with
  an `AnonymousObjectCreationExpressionSyntax` expression body) → one name per
  initializer, in source order. Only implicit-name initializers (`e.A`, a bare
  `MemberAccessExpressionSyntax`) are supported; an initializer with an explicit
  `NameEquals` (`new { Key = e.A }`) is treated as unreadable (see below).
- **String, single:** `"Id"` (one `LiteralExpressionSyntax` string argument) →
  `["Id"]`.
- **String array (params), composite:** `"A", "B"` (multiple string-literal
  arguments) → `["A", "B"]` in argument order.
- **Anything else** (method call, ternary, block-bodied lambda, non-literal string
  array element, explicit-name anonymous-type member, etc.) → new diagnostic code
  `UnreadableHasKeyArgument`, entity's key left unset for this call, consistent with
  the "surface via diagnostics, don't silently drop" rule from Priority 0.

If multiple `HasKey(...)` calls exist for the same entity (not valid EF but
syntactically possible), each produces its own `KeyConfig`; `ModelMerger` resolves
ambiguity by taking the first (see below) — same `FirstOrDefault` convention already
used for property-level configs.

## Merging: `ModelMerger.ApplyKeys`

```csharp
public static EntityModel ApplyKeys(EntityModel entity, IReadOnlyList<KeyConfig> configs)
```

Looks up a config by `EntityName` only (no property-name join, since this is one
config per entity, not per property) and sets `KeyPropertyNames`; no match leaves it
`[]`. Separate composed call, consistent with how `ApplyMaxLengths`/`ApplyIsRequired`
are composed rather than orchestrated — callers who want all three call all three.

## Rewriting: `OnModelCreatingRewriter`

Two new public methods, reusing existing private helpers
(`FindEntityConfigInvocations`, `FindCallsNamed`, `FindOnModelCreatingMethod`,
`BuildEntityInvocationStatement`) with new `HasKey`-specific leaf builders.

```csharp
public string SetKey(string sourceCode, string entityName, IReadOnlyList<string> propertyNames)
```

Always emits the canonical lambda form on write, regardless of how (or whether) the
key was originally expressed:

- One property name → `e => e.Id`.
- Multiple property names → `e => new { e.A, e.B }`.

Same four-case dispatch as `RewriteMaxLength`:

1. **Mutate existing `entity.HasKey(...)` call.** Replace its argument list with the
   canonical lambda form built from `propertyNames`.
2. **Insert a new `entity.HasKey(...)` statement** into an existing
   `Entity<T>(entity => { ... })` block that has no `HasKey` call yet, same shape as
   `InsertPropertyStatement` (appended as its own statement, not chained onto a
   `Property(...)` call — `HasKey` never chains off `Property`).
3. **Synthesize a whole new `modelBuilder.Entity<T>(entity => { ... })` block**
   containing just the `HasKey(...)` statement, when the entity isn't configured at
   all yet, same shape as `InsertEntityBlock`.

(There is no "append to existing bare call" case here the way `HasMaxLength`/
`IsRequired` append onto a bare `Property(...)` call — `HasKey` is never chained onto
anything, so mutate/insert-statement/synthesize-block covers every case.)

```csharp
public string RemoveKey(string sourceCode, string entityName)
```

Finds the matching `entity.HasKey(...)` statement and removes it entirely (unlike
`RemoveMaxLength`/`RemoveIsRequired`, which replace the call with its receiver
expression — `HasKey` has no bare receiver to fall back to, since it isn't chained off
`Property()`). No-op (returns `sourceCode` unchanged) if no such call exists.

## New diagnostic code

`UnreadableHasKeyArgument` — entity name populated, `PropertyName: null` (this is an
entity-level config, so there's no single property to attribute it to), span pointing
at the unreadable argument/call.

## Testing

New test coverage mirroring the existing `HasMaxLength`/`IsRequired` suites:

- `FluentConfigParserTests`: single-property lambda, composite lambda (2+ properties,
  order preserved), single string, string-array composite, explicit-name anonymous
  member → diagnostic, non-literal string array element → diagnostic, method-call
  argument → diagnostic, no `HasKey` call present → empty `KeyPropertyNames`.
- `ModelMergerTests`: config applies `KeyPropertyNames`; no config leaves it empty;
  `Properties` untouched by the merge.
- `OnModelCreatingRewriterTests`: `SetKey` for all three insertion cases (mutate,
  insert-statement, synthesize-block) for both single and composite keys, plus
  `RemoveKey` (existing call removed; no-op when absent).

## Out of scope

- No validation that `KeyPropertyNames` actually reference properties that exist on
  the entity, or that they're not duplicated — the model simply stores what the
  fluent call said.
- No support for `[Key]` data-annotation attributes (CLR-side) — this item is fluent
  API only, matching how `IsRequiredOverride` stays separate from CLR-derived
  `IsNullable`.
- No UI consumer yet (Blazor shell is Priority 4) — this is model/parse/rewrite only.
- Explicit-name anonymous-type initializers (`new { Key1 = e.A }`) are read as
  unreadable rather than resolved — a plausible but rarer shape, deferred like the
  parenthesized-lambda gap already tracked in the backlog.
