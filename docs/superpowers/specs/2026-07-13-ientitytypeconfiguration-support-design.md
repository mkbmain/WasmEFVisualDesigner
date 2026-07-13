# `IEntityTypeConfiguration<T>` support — Design

**Status:** approved, ready for planning
**Backlog item:** Priority 3 — `[spec/plan] IEntityTypeConfiguration<T> support`

## Problem

Every `FluentConfigParser.Parse*` method only recognizes config written as
`modelBuilder.Entity<T>(entity => { ... })` (or the bare-chained
`modelBuilder.Entity<T>()...` variant for relationships). EF's other first-class config
style — one `IEntityTypeConfiguration<T>` class per entity, each implementing
`Configure(EntityTypeBuilder<T> builder)` — is invisible. This is EF's own recommended
pattern for larger models and was called out in the original design doc as a deliberate
fast-follow once the harder `OnModelCreating` case was solid (it now is: all 10
`Parse*`/`Apply*` pairs are shipped).

**Scope of this pass: parsing only, full config-kind parity.** All 10 existing
`FluentConfigParser.Parse*` methods gain the ability to also read `IEntityTypeConfiguration<T>`
classes, merging into the same `EntityModel`/`RelationshipModel` via the existing,
unchanged `ModelMerger`. No rewriter — matching the precedent set by relationships,
which also shipped parse+merge only, since there is still no diagram/editor UI to
validate a write-back shape against.

## Why this generalizes cheaply

`FluentSyntaxHelpers.FindCallsNamed(SyntaxNode scope, string methodName)` and
`GetPropertyNameFor`/`GetPropertyNameForPropertyCall` already only care that `scope` is
*some* `SyntaxNode` to walk — they never inspect the receiver identifier (that
receiver-agnosticism was itself a P0 fix: "hardcoded `modelBuilder` receiver name"). A
`Configure` method body is just as valid a scope as an `Entity<T>(entity => {...})`
lambda block. The only new work is *finding* that scope and its entity name; every
downstream call-reading primitive is reused verbatim.

## New helper: `FluentSyntaxHelpers.FindConfigurationScopes`

```csharp
internal static IEnumerable<(string EntityName, SyntaxNode Scope)> FindConfigurationScopes(
    CompilationUnitSyntax root)
```

Yields every `(entityName, scope)` pair in the source, from either shape:

1. **`Entity<T>(...)` style** (existing behavior, unchanged): every invocation where
   `GetConfiguredEntityName` resolves a name — `Scope` is the invocation itself. One
   entity name can yield multiple scope entries if configured across multiple
   `Entity<T>()` blocks in the file (already true today).
2. **`IEntityTypeConfiguration<T>` style** (new): every `ClassDeclarationSyntax` whose
   base list contains `IEntityTypeConfiguration<T>` — matched via a small
   `TryGetEntityTypeConfigurationTypeArgument(BaseTypeSyntax)` helper that accepts both
   the bare form (`GenericNameSyntax`) and the qualified form
   (`QualifiedNameSyntax { Right: GenericNameSyntax }`, e.g.
   `Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Product>`) — with a method
   named `Configure` inside the class. `EntityName` is `T`'s text; `Scope` is the
   `Configure` `MethodDeclarationSyntax`. A class implementing the interface with no
   `Configure` method found is silently skipped — zero scope entries for it, no
   diagnostic, consistent with a file having zero `Entity<T>()` calls today.

Multiple `IEntityTypeConfiguration<T>` classes in one file are all discovered
(`DescendantNodes<ClassDeclarationSyntax>()`, not `.First()`). A file may mix both
styles freely; nothing in this design assumes one style per file.

## `FluentConfigParser` integration

All 10 `Parse*` methods (`ParseMaxLengths`, `ParsePrecisions`, `ParseIsRequired`,
`ParseKeys`, `ParseTableMappings`, `ParseColumnNames`, `ParseColumnTypes`,
`ParseDefaultValues`, `ParseIndexes`, `ParseRelationships`) currently repeat the same
boilerplate:

```csharp
var entityNames = root.DescendantNodes()
    .OfType<InvocationExpressionSyntax>()
    .Select(FluentSyntaxHelpers.GetConfiguredEntityName)
    .Where(name => name is not null)
    .Distinct()!;

foreach (var entityName in entityNames)
    foreach (var entityInvocation in FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName!))
        foreach (var call in FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "..."))
            ...
```

This is replaced, mechanically, in all 10 methods, with:

```csharp
foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
    foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "..."))
        ...
```

No signature changes to any `Parse*` method — `ParseRelationships(string sourceCode,
IReadOnlyList<EntityModel> entities)` keeps its existing signature too. Callers keep
calling `ParseMaxLengths(sourceCode)` etc. exactly as before; the method transparently
recognizes whichever style (or mix of styles) is in the source text. This is also a net
simplification: the 10-way-duplicated entity-name-scan boilerplate collapses to one
shared helper.

`FindEntityConfigInvocations` and `GetConfiguredEntityName` are unchanged and remain in
use *inside* `FindConfigurationScopes` for the first style — no existing behavior for
`Entity<T>()`-style files changes.

### Relationships special case

`ParseRelationships` additionally supports the bare-chained style —
`modelBuilder.Entity<Order>().HasOne(...).WithMany(...)` — via
`FindChainedCall(entityInvocation, "HasOne")` / `"HasMany"`, called directly on the
scope invocation. That specific lookup only makes sense when `Scope` *is* an
`InvocationExpressionSyntax`; there's no equivalent "chained directly on the scope"
shape for a `Configure` method body (a method declaration can't have `.HasOne(...)`
chained onto it). This call is guarded with `if (scope is InvocationExpressionSyntax
entityInvocation)`, skipped for `IEntityTypeConfiguration<T>` scopes — those only ever
find relationships via the block-nested-style lookup (`FindCallsNamed(scope, "HasOne")`
/ `"HasMany"`), which for a `Configure` method body means directly inside the method,
the natural way relationships are written there:

```csharp
public void Configure(EntityTypeBuilder<Order> builder)
{
    builder.HasOne(o => o.Customer)
        .WithMany(c => c.Orders)
        .HasForeignKey(o => o.CustomerId);
}
```

Everything else in relationship parsing (target-entity resolution via
`entities`, `HasForeignKey`/`OnDelete`/`UsingEntity` chain-walking) is scope-shape-agnostic
already and needs no change.

## `ModelMerger` — no changes

Every `Apply*` method merges purely by `(EntityName, PropertyName)` (or `EntityName`
alone for entity-level configs like keys/table mapping), read from the config DTOs. It
has never known or cared which file, or which style, a config came from. This is exactly
why the "transparent" API decision (no new public methods) works: the merge layer is
already source-agnostic, so extending only the parse layer is sufficient for full
parity.

## Testing

**`FluentSyntaxHelpersTests`** (new cases, alongside the existing file):
- `FindConfigurationScopes` finds a single `IEntityTypeConfiguration<T>` class's
  `Configure` method as a scope
- Finds multiple `IEntityTypeConfiguration<T>` classes in one file
- Finds both an `Entity<T>()` block and an `IEntityTypeConfiguration<T>` class in one
  file (mixed style)
- Qualified interface name (`Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<T>`)
  resolves the same as the bare form
- A class implementing the interface with no `Configure` method yields no scope entry,
  no diagnostic
- A class implementing an unrelated generic interface (e.g. `IValidatableObject`) is
  ignored

**`FluentConfigParserTests`** (new cases, one representative per existing `Parse*`
suite, using `IEntityTypeConfiguration<T>`-shaped source instead of `Entity<T>()`):
- `ParseMaxLengths` reads `HasMaxLength` from inside `Configure`
- `ParsePrecisions`, `ParseIsRequired`, `ParseKeys`, `ParseTableMappings`,
  `ParseColumnNames`, `ParseColumnTypes`, `ParseDefaultValues`, `ParseIndexes` — same,
  one case each, confirming the shared-helper swap didn't regress any config kind
- `ParseRelationships`: block-nested-equivalent style inside `Configure` (the only
  style available for this shape, per the special case above), including a case
  cross-referencing `entities` to resolve a nav-only target, and a case confirming the
  bare-chained relationship style is *not* incorrectly matched against a
  `Configure` method scope
- A file mixing both styles for two different entities — both parse correctly in one
  call
- Existing `Entity<T>()`-only test fixtures across all 10 methods still pass unchanged
  (regression coverage for the mechanical refactor)

**`ModelMergerTests`**: no new tests needed — merge behavior is proven source-agnostic
by construction; existing tests already cover it via `Entity<T>()`-sourced configs.

## Out of scope (this pass)

- The rewriter — write-back into `IEntityTypeConfiguration<T>` classes — deferred to a
  follow-up spec, matching the relationships precedent: no diagram/editor UI exists yet
  to validate a write-back shape against.
- Explicit interface implementation (`void IEntityTypeConfiguration<Product>.Configure(...)`)
  — only the ordinary implicit `Configure` method is matched.
- Multi-file assembly of config classes (e.g. simulating `ApplyConfigurationsFromAssembly`
  across a whole project) — each `Parse*(sourceCode)` call operates on one file's text,
  same as today; assembling a whole project's files together is an orchestration
  concern for the future app shell (Priority 4), not this parser.
- A `Configure` method with an unexpected parameter shape (not `EntityTypeBuilder<T>`)
  is still matched by name alone — `IEntityTypeConfiguration<T>`'s contract guarantees
  the real signature, so no additional validation is added.
- No UI consumer (Blazor shell is Priority 4).
