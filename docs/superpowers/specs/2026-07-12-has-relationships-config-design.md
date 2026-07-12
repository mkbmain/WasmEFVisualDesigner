# Relationships (`HasOne`/`HasMany`/`WithOne`/`WithMany`) — Design

**Status:** approved, ready for planning
**Backlog item:** Priority 2 — `[spec] Relationships — 1:1, 1:many, many:many`

## Problem

Nothing today models EF Core relationships. `HasOne`/`HasMany`/`WithOne`/`WithMany` chains
(1:1, 1:many, many:many, including `HasForeignKey`, `OnDelete`, and `UsingEntity`) are
invisible to the parser and merger. This is the largest remaining Priority 2 item and,
unlike the others, needs its own scoping decisions before the established
parse → merge → rewrite pattern can apply cleanly.

**Scope of this pass: parse + merge only.** No rewriter (`SetRelationship`/
`RemoveRelationship`). Every other Priority 2 item shipped full round-trip in one pass,
but relationships are large enough (four chain shapes, cross-entity resolution) that
adding rewrite now would mean picking a canonical write-side/form without a diagram
consumer yet to validate it against — matching the original spec's own sequencing
(read-only diagram render before editable diagram/rewrite work). The rewriter is a
follow-up spec once these shapes are validated against real usage.

## Why this differs from every prior Priority 2 item

Every `FluentConfigParser.Parse*` method so far takes only `string sourceCode` — it
resolves everything (property names, argument values) from the `OnModelCreating`
method's own text. Relationships break that: `entity.HasOne(o => o.Customer)` only
gives us the navigation property name (`"Customer"`), not the related entity's type.
This project has no semantic model (pure syntax-tree parsing, no `CSharpCompilation`),
so the only way to resolve `"Customer"` → entity `Customer` is to cross-reference the
already-parsed POCO class — `EntityClassParser` already captures
`PropertyModel.ClrType` for exactly this kind of lookup.

**Signature:**
```csharp
public ParseResult<IReadOnlyList<RelationshipConfig>> ParseRelationships(
    string sourceCode,
    IReadOnlyList<EntityModel> entities)
```
This is the one asymmetric method in `FluentConfigParser` — callers already have the
`EntityModel`s from `EntityClassParser` by the time they parse fluent config, so this
is just one more parameter, explicit about the real dependency.

## Model changes

**New `RelationshipKind` enum** (`Model/RelationshipKind.cs`):
```csharp
public enum RelationshipKind
{
    OneToOne,
    OneToMany,
    ManyToMany,
}
```

**New `RelationshipModel`** (`Model/RelationshipModel.cs`):
```csharp
public sealed record RelationshipModel(
    string PrincipalEntity,
    string DependentEntity,
    RelationshipKind Kind,
    string? PrincipalNavigation,
    string? DependentNavigation,
    IReadOnlyList<string>? ForeignKeyProperties = null,
    string? OnDeleteBehavior = null,
    string? JoinEntityName = null)
{
    public IReadOnlyList<string> ForeignKeyProperties { get; init; } = ForeignKeyProperties ?? new List<string>();
}
```

- For `OneToMany`: `PrincipalEntity` is the "one" side, `DependentEntity` is the "many"
  side (holds the FK). `PrincipalNavigation` is the collection nav on the principal
  (null if unidirectional / bare `WithMany()`/`HasMany()`), `DependentNavigation` is the
  reference nav on the dependent (null if bare `HasOne()`/`WithOne()`).
- For `OneToOne`: `DependentEntity` holds the FK (see resolution rule below).
  `PrincipalNavigation`/`DependentNavigation` follow the same nullability rule.
- For `ManyToMany`: `PrincipalEntity`/`DependentEntity` are nominal only (the entity the
  chain started on vs. the entity named in `HasMany`'s target) — there is no real
  principal/dependent distinction. `ForeignKeyProperties` is always empty and
  `OnDeleteBehavior` is always null for this kind. `JoinEntityName` is set only when
  `.UsingEntity<TJoin>()` names an explicit join entity type; null for the default
  implicit shared-type join entity.
- `RelationshipModel` is not nested under `EntityModel` — there is no existing
  "whole schema" aggregate type in this codebase (every `ModelMerger` method today is
  `Apply*(EntityModel, configs) -> EntityModel`, one entity at a time). Introducing a
  schema-root type now would mean guessing the eventual Blazor-shell aggregate shape
  before that shell has been designed (Priority 4). Relationships instead live as a
  flat, independent list; a caller holds `List<EntityModel>` and
  `IReadOnlyList<RelationshipModel>` side by side however it likes.

## New DTO: `RelationshipConfig`

```csharp
// src/EfSchemaVisualizer.Core/Parsing/RelationshipConfig.cs
public sealed record RelationshipConfig(
    string PrincipalEntity,
    string DependentEntity,
    RelationshipKind Kind,
    string? PrincipalNavigation,
    string? DependentNavigation,
    IReadOnlyList<string>? ForeignKeyProperties = null,
    string? OnDeleteBehavior = null,
    string? JoinEntityName = null)
{
    public IReadOnlyList<string> ForeignKeyProperties { get; init; } = ForeignKeyProperties ?? new List<string>();
}
```
Mirrors `RelationshipModel` field-for-field (no transformation needed at merge time
beyond wrapping in the model type — same 1:1 mapping precedent as `IndexConfig`/`IndexModel`).

## Call discovery

All existing `Parse*` methods find calls *nested inside* an `Entity<T>(entity => { ... })`
lambda block via `FluentSyntaxHelpers.FindCallsNamed`, which only walks descendants.
Relationships are idiomatically written **without** that lambda:

```csharp
modelBuilder.Entity<Order>()
    .HasOne(d => d.Customer)
    .WithMany(p => p.Orders)
    .HasForeignKey(d => d.CustomerId);
```

Here `.HasOne(...)` wraps the bare `Entity<Order>()` invocation rather than nesting
inside it, so `FindCallsNamed` alone would never see it. Both styles are valid EF and
both must be supported:

1. **Block-nested style** (`entity.HasOne(...)` inside the lambda) — found via the
   existing `FindCallsNamed(entityInvocation, "HasOne")` / `"HasMany"`, unchanged.
2. **Chained, no-lambda style** — found via a new
   `FluentSyntaxHelpers.FindChainedCall(InvocationExpressionSyntax invocation, string methodName)`:
   climbs exactly one link outward (`invocation.Parent` is a
   `MemberAccessExpressionSyntax` whose `Name.Identifier.Text == methodName` and whose
   `Parent` is the wrapping `InvocationExpressionSyntax`) and returns that invocation or
   null. Called with `entityInvocation` itself, checking for `"HasOne"` and `"HasMany"`.

`FindChainedCall` is also the primitive used for the rest of the chain, below.

### Chain shape, once a `HasOne`/`HasMany` call is found

1. **`WithMany`/`WithOne` must be the immediate next chained call.** EF's builder
   return types force this (nothing else can be inserted between `HasOne(...)` and its
   `With*`). Found via `FindChainedCall(hasOneOrHasManyCall, "WithMany")` /
   `"WithOne"`. If neither is found, the chain is incomplete/malformed for our purposes
   (half-written code) — skip silently, no diagnostic.
2. **`HasForeignKey` / `HasForeignKey<T>` / `OnDelete` / `UsingEntity` /
   `UsingEntity<T>` can appear in any order** after the `With*` call. Found by an
   outward scan from the `With*` invocation — structurally the same idea as the
   existing `TryReadIsUnique` walk used for `HasIndex().IsUnique()` (climb
   `MemberAccessExpressionSyntax`/`InvocationExpressionSyntax` pairs until the
   enclosing `ExpressionStatement`), written fresh for relationships rather than
   refactoring that working, tested code — consistent with the precedent set for
   `TryReadKeyPropertyNames`/`TryReadIndexPropertyNames` staying separate despite overlap.

## Resolving the related entity

Needed for `HasOne`/`HasMany`'s target, and for `HasForeignKey<T>`'s dependent-side
disambiguation in 1:1. Two ways, tried in order:

1. **Explicit generic argument** — `HasOne<Customer>(...)` / `HasMany<Tag>(...)` — the
   type name is directly in the syntax (`GenericNameSyntax.TypeArgumentList`), no
   cross-referencing needed.
2. **Navigation lambda** — `d => d.Customer` (or `(Order d) => d.Customer`, reusing the
   parenthesized-lambda support already added to `GetPropertyNameForPropertyCall`) gives
   a property name. Look it up in the *configuring entity's* `PropertyModel.ClrType`
   (from the `entities` parameter). For collection-typed navigations
   (`ICollection<Order>`, `List<Order>`, `IList<Order>`, `IEnumerable<Order>`,
   `HashSet<Order>`, `ISet<Order>`, `Order[]`) strip the wrapper via a small helper,
   `FluentSyntaxHelpers.TryGetElementTypeName(string clrType)`, to get the element type
   name. A bare (non-collection) `ClrType` is used as-is.

If neither resolves (nav property not found on the entity's parsed properties, or an
unrecognized wrapper shape) → diagnostic `UnresolvableRelationshipTarget` (entity name
set, `PropertyName: null`, span of the `HasOne`/`HasMany` call); the relationship is
skipped.

## The four shapes

| Chain | Kind | Principal | Dependent | Notes |
|---|---|---|---|---|
| `entity.HasOne(nav?).WithMany(nav?)` | `OneToMany` | resolved target | configuring entity | dependent holds the FK |
| `entity.HasMany(nav?).WithOne(nav?)` | `OneToMany` | configuring entity | resolved target | principal holds the collection |
| `entity.HasOne(nav?).WithOne(nav?)` | `OneToOne` | see below | see below | ambiguous without help |
| `entity.HasMany(nav?).WithMany(nav?)` | `ManyToMany` | configuring entity | resolved target | nominal only |

**`OneToOne` dependent resolution:** the type named in `HasForeignKey<TDependent>(...)`'s
explicit generic argument, if present; otherwise defaults to the configuring entity
(documented assumption — real 1:1 EF code virtually always specifies this generic to
disambiguate, since EF itself requires it in ambiguous cases).

**Nav names on `HasOne`/`HasMany`/`WithOne`/`WithMany`** are all optional (any of these
calls may be bare, e.g. `.WithMany()`, meaning no inverse navigation property exists —
`PrincipalNavigation`/`DependentNavigation` are simply null in that case, not a
diagnostic).

## `HasForeignKey` argument reading

Same three shapes as `HasKey`: single lambda (`d => d.CustomerId`), composite lambda
(`d => new { d.A, d.B }`), bare string params (single or composite). The existing
private `FluentConfigParser.TryReadKeyPropertyNames`/
`TryReadKeyPropertyNamesFromLambdaBody` logic is promoted to a new shared
`FluentSyntaxHelpers.TryReadPropertyNameList(InvocationExpressionSyntax call)` — this is
the same argument-shape logic verbatim, a genuine shared primitive rather than a
cosmetic refactor. `ParseKeys` is updated to call the shared helper instead of its
private copy; behavior is unchanged.

Unreadable shape → diagnostic `UnreadableHasForeignKeyArgument` (entity = dependent
entity name, `PropertyName: null`, span of the `HasForeignKey` call); the relationship
is still recorded with `ForeignKeyProperties = []` (a shadow FK is valid EF — this
mirrors "no `HasForeignKey` call at all" below).

**No `HasForeignKey` call in the chain at all** → `ForeignKeyProperties = []`, no
diagnostic (EF creates a convention-based shadow FK; this is ordinary, valid code).

## `OnDelete`

Captures the `DeleteBehavior.X` member-access text as a raw string (`"Cascade"`,
`"Restrict"`, `"SetNull"`, `"NoAction"`, `"ClientSetNull"`, `"ClientCascade"`,
`"ClientNoAction"`) — same treatment as `ColumnType`/`DefaultValueLiteral` elsewhere in
the codebase (store the literal text, no enum parsing/validation). A non-member-access
argument (e.g. a variable) → diagnostic `UnreadableOnDeleteArgument` (entity =
dependent entity name, `PropertyName: null`, span of the argument);
`OnDeleteBehavior` left null. No `OnDelete` call in the chain → `OnDeleteBehavior = null`,
no diagnostic.

## `UsingEntity`

- `.UsingEntity<Join>(...)` (any arguments/lambda body) → `JoinEntityName = "Join"`
  (from the generic argument only).
- Bare `.UsingEntity(...)` (configuring the implicit shared-type join entity, e.g.
  `j => j.ToTable("PostTags")`) → `JoinEntityName = null`, no diagnostic.
- No `UsingEntity` call at all → `JoinEntityName = null`, no diagnostic (EF creates an
  implicit shared-type join entity automatically).

The lambda body contents of `UsingEntity(...)` (nested `HasOne`/`HasOne` two-sided FK
config, `ToTable`, etc.) are out of scope — same treatment as `HasDefaultValueSql`
elsewhere in this codebase.

## New diagnostic codes

Added to `DiagnosticCodes`:

| Code | When | PropertyName |
|---|---|---|
| `UnresolvableRelationshipTarget` | `HasOne`/`HasMany` nav can't be resolved to a known entity | null (entity-level) |
| `UnreadableHasForeignKeyArgument` | `HasForeignKey` argument shape not recognized | null (entity-level) |
| `UnreadableOnDeleteArgument` | `OnDelete` argument is not a `DeleteBehavior.X` member access | null (entity-level) |

## Merging: `ModelMerger.ApplyRelationships`

```csharp
public static IReadOnlyList<RelationshipModel> ApplyRelationships(
    IReadOnlyList<RelationshipConfig> configs)
```

A flat 1:1 map from `RelationshipConfig` to `RelationshipModel` — no entity list needed
since each config already carries both entity names by name (consistent with the
decision to keep relationships independent of any schema-root aggregate). Callers hold
this list alongside their `List<EntityModel>` however they like.

## Testing

**`FluentConfigParserTests`** (new cases):
- `HasOne(nav).WithMany(nav)` — block-nested style, resolves both nav names, `Kind = OneToMany`, dependent = configuring entity
- Same shape, chained-off-`Entity<T>()` style (no lambda) — same result
- `HasMany(nav).WithOne(nav)` — principal = configuring entity
- `HasOne(nav).WithOne(nav).HasForeignKey<TDependent>(fk)` — explicit generic sets dependent
- `HasOne(nav).WithOne(nav)` with no `HasForeignKey<T>` generic — dependent defaults to configuring entity
- `HasMany(nav).WithMany(nav)` — `Kind = ManyToMany`
- `HasMany(nav).WithMany(nav).UsingEntity<Join>()` — `JoinEntityName = "Join"`
- `HasMany(nav).WithMany(nav).UsingEntity(j => j.ToTable(...))` — `JoinEntityName = null`, no diagnostic
- Explicit generic target (`HasOne<Customer>()`) with no nav lambda
- Bare `WithMany()`/`WithOne()` — no inverse nav, `PrincipalNavigation`/`DependentNavigation` null
- Composite `HasForeignKey(d => new { d.A, d.B })`
- Bare string `HasForeignKey("CustomerId")` / composite string params
- No `HasForeignKey` at all → empty `ForeignKeyProperties`, no diagnostic
- Unreadable `HasForeignKey` shape → `UnreadableHasForeignKeyArgument`, relationship still recorded with empty FK list
- `.OnDelete(DeleteBehavior.Cascade)` present; absent; unreadable arg → `UnreadableOnDeleteArgument`
- Unresolvable nav (property not found on entity) → `UnresolvableRelationshipTarget`, relationship skipped
- Unrecognized collection wrapper on nav property → same diagnostic
- Malformed chain (`HasOne(...)` with no following `WithMany`/`WithOne`) — silently skipped, no diagnostic
- `HasForeignKey`/`OnDelete`/`UsingEntity` in varying order after `With*` — all resolve the same

**`ModelMergerTests`**:
- `ApplyRelationships` maps a list of configs to models field-for-field
- Empty input → empty output

**`FluentSyntaxHelpersTests`** (new file — no direct tests of this internal helper class exist yet):
- `TryReadPropertyNameList` — same cases as existing `TryReadKeyPropertyNames` coverage, to confirm the promoted helper is behavior-preserving
- `TryGetElementTypeName` — each supported wrapper shape, plus a non-collection type passed through unchanged, plus an unrecognized wrapper returning null

## Out of scope (this pass)

- The rewriter (`SetRelationship`/`RemoveRelationship`) — separate follow-up spec once
  these shapes are validated against real usage and the diagram exists to clarify what
  "editing a relationship" needs to support in the UI.
- `UsingEntity`'s nested join-config lambda contents (nested `HasOne`/`HasOne`,
  `ToTable`, etc. inside the join entity configuration).
- Duplicate detection when the same relationship is configured redundantly from both
  sides (e.g. both `Order.HasOne(...).WithMany(...)` and
  `Customer.HasMany(...).WithOne(...)` for the same logical relationship) — it will
  appear twice in the model. Known limitation, consistent with the `HasIndex` precedent
  ("no duplicate/collision detection").
- Data-annotation attributes (`[ForeignKey]`, `[InverseProperty]`, `[Required]` on
  navigations) — fluent-API only, consistent with the rest of the engine.
- Validation that resolved FK/nav property names actually exist as properties on the
  target entity (only the *navigation* property's existence is checked, to resolve the
  target type).
- `HasPrincipalKey` (configuring the FK to point at a non-primary-key principal
  property) — not read; principal key is assumed to be the primary key.
- No UI consumer (Blazor shell is Priority 4).
