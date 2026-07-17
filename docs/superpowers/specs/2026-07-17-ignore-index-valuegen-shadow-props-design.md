# Ignore, `[Index]` attribute, value generation, shadow properties — design

> Addresses four Round 3 Priority 3 backlog items in
> `docs/backlog.md`: `Ignore`/`Ignore<T>()` unread, `[Index]` class-level
> attribute unread, value generation unread, shadow properties dropped.

## Goal

Four EF Core constructs are silently dropped by the parser today, each
causing a specific wrongness in the rendered diagram:

1. **`Ignore`** — `entity.Ignore(e => e.X)` / `entity.Ignore("X")` (property-level)
   and `modelBuilder.Ignore<T>()` (whole-entity). Ignored members/entities
   still render as mapped columns/tables.
2. **`[Index]`** class-level attribute — the annotation-based equivalent of
   `HasIndex`, common since EF Core 5 (scaffold emits it). Indexes declared
   this way are invisible.
3. **Value generation** — `ValueGeneratedOnAdd()`/`OnUpdate()`/`OnAddOrUpdate()`/
   `Never()`, `UseIdentityColumn()`. Identity columns are near-universal in
   real schemas and currently unmarked.
4. **Shadow properties** — `entity.Property<T>("Name")` with no CLR member.
   The config is silently unmatched and vanishes during merge.

Scope for this pass: parsing + model + read-only diagram surfacing only.
No rewriter (write-back) work — matches how `[Index]`/annotation support
landed previously (parse+merge only, no edit path), since editing these
constructs has no existing UI precedent to extend and isn't part of the
four backlog items as written.

## 1. Ignore

### Property-level: `entity.Ignore(e => e.X)` / `entity.Ignore("X")`

- New `FluentConfigParser.ParseIgnoredProperties(string sourceCode)` returning
  `ParseResult<IReadOnlyList<IgnoreConfig>>`, `IgnoreConfig(string EntityName, string PropertyName)`
  living in `Merging/IgnoreConfig.cs` (matches the existing `*Config` DTO convention).
- Within each `FluentSyntaxHelpers.FindConfigurationScopes` scope, find calls
  named `Ignore` via `FindCallsNamed(scope, "Ignore")`. Resolve the property
  name the same way `GetPropertyNameForPropertyCall` does for `Property(...)`:
  lambda member access (expression-bodied and single-`return` block-bodied,
  both `Simple`/`Parenthesized` lambda forms) or a string literal argument.
  Unresolvable shapes emit a new `DiagnosticCodes.UnreadableIgnoreArgument`
  diagnostic (mirrors `UnreadableHasIndexArgument`).
- `ModelMerger.ApplyIgnoredProperties(EntityModel entity, IReadOnlyList<IgnoreConfig> configs)`
  filters `entity.Properties` to drop any property whose name matches a
  config for that entity (same `IndexByProperty`-adjacent lookup shape as
  the other per-property `Apply*` methods — a `HashSet<string>` of ignored
  names is enough since there's no scalar value to keep, just an exclusion).
- Add `"Ignore"` to `FluentConfigParser.RecognizedCallNames`.

### Whole-entity: `modelBuilder.Ignore<T>()`

- New `FluentConfigParser.ParseIgnoredEntities(string sourceCode)` returning
  `IReadOnlyList<string>` (entity type names) — no diagnostics needed, this
  shape is unambiguous (generic type argument, no lambda to misparse).
- Distinct from the property-level call: this is a bare invocation directly
  on the `modelBuilder` receiver, *not* nested inside any
  `Entity<T>()`/`IEntityTypeConfiguration` scope. Implementation: walk
  top-level statements in `OnModelCreating`'s body (or the compilation unit
  for a bare-statement `ConfigSource`, matching how `FindConfigurationScopes`
  itself locates its scopes) for invocations named `Ignore` with a generic
  type argument, that are *not* nested inside another scope's span. Reuse
  `FluentSyntaxHelpers.TryGetGenericTypeArgument` (already exists, used for
  relationship principal-type resolution) to read `T`.
- `DiagramModelBuilder.Build` drops entities whose name is in this set
  *before* any other merge step runs (simplest correct behavior — an
  ignored entity contributes nothing, including relationships referencing
  it; those relationships will fail to resolve their related entity and are
  already handled by the existing "unresolvable relationship" diagnostic
  path used elsewhere).

## 2. `[Index]` class-level attribute

- `EntityClassParser` already parses class-level attributes for `[Table]`
  (see the 2026-07-16 annotation-parsing design) — same location, same
  pattern. Add handling for `[Index(...)]`, which:
  - Takes one or more `nameof(Property)` (or bare string) constructor
    arguments as the indexed property names.
  - Allows `AllowMultiple = true` (a class can carry several `[Index]`
    attributes) — iterate all matching attributes on the class, not just
    the first.
  - Supports named arguments `IsUnique` (bool) and `Name` (string), both
    optional.
- Produces the same `IndexConfig(EntityName, PropertyNames, IsUnique, Name)`
  DTO the fluent `HasIndex` path already produces, so no new merge method is
  needed — `ModelMerger.ApplyIndexes` already takes a flat
  `IReadOnlyList<IndexConfig>`.
- Conflict resolution (fluent wins): `DiagramModelBuilder.Build` concatenates
  annotation-derived `IndexConfig`s before fluent-derived ones and dedupes by
  `(EntityName, PropertyNames-as-ordered-set)`, keeping the last (fluent) one
  on conflict — same shape as the existing relationship dedupe key
  (`RelationshipDedupeKey`), added as a sibling `IndexDedupeKey` helper.
- Unresolvable `nameof(...)`/string arguments emit
  `DiagnosticCodes.UnreadableHasIndexArgument` (reuse; it's already
  worded generically as "argument(s) could not be read as property name(s)").

## 3. Value generation

- `PropertyModel` gains `string? ValueGenerated = null`, one of the literal
  strings `"OnAdd"`, `"OnUpdate"`, `"OnAddOrUpdate"`, `"Never"`, `"Identity"`
  (a plain string, not an enum, matching how `OnDeleteBehavior` on
  `RelationshipModel` is already modeled as `string?` rather than an enum —
  consistent with the codebase's existing precedent for EF-vocabulary
  fields that only ever get displayed, never branched on, in C#).
- New `Merging/ValueGenerationConfig.cs`:
  `ValueGenerationConfig(string EntityName, string PropertyName, string Mode)`.
- `FluentConfigParser.ParseValueGeneration(string sourceCode)`: within each
  config scope, for each `Property(...)` call, walk its chained calls (reuse
  `FluentSyntaxHelpers.GetPropertyNameFor`'s chain-walking approach, or more
  directly: for every call named one of `ValueGeneratedOnAdd`,
  `ValueGeneratedOnUpdate`, `ValueGeneratedOnAddOrUpdate`, `ValueGeneratedNever`,
  `UseIdentityColumn`, resolve its owning property via
  `FluentSyntaxHelpers.GetPropertyNameFor`, same as every other
  `Property()`-chained config parser). Map the method name to the `Mode`
  string (`UseIdentityColumn` → `"Identity"`).
- `ModelMerger.ApplyValueGeneration` folds it into `PropertyModel.ValueGenerated`,
  same per-property `IndexByProperty` shape as `ApplyMaxLengths` etc.
- Add all five call names to `RecognizedCallNames`.
- UI: `EntityNode.razor` shows a small read-only badge/label next to the
  property name (e.g. `Identity`, `On Add`) when `ValueGenerated is not null`.
  No editor — matches your answer scoping this to "parse + read-only badge".

## 4. Shadow properties

- `PropertyModel` gains `bool IsShadow = false`.
- New `Merging/ShadowPropertyConfig.cs`:
  `ShadowPropertyConfig(string EntityName, string PropertyName, string ClrType)`.
- `FluentConfigParser.ParseShadowProperties(string sourceCode)`: within each
  config scope, find `Property<T>(...)` calls (generic invocation — the
  existing `Property` handling doesn't require genericity so this is an
  additive case, matched by `((GenericNameSyntax)memberAccess.Name).TypeArgumentList`
  being non-empty) whose sole/first argument is a string literal (the
  scaffold/idiomatic shadow-property shape; a lambda can't reference a
  non-existent member so only the string-literal overload is meaningful
  here). Extract the type argument as `ClrType` (`TryGetGenericTypeArgument`
  reused) and the literal as `PropertyName`.
- Merge happens in `DiagramModelBuilder.Build` (not `ModelMerger`, since it
  needs to know which property names *don't* already exist on the entity,
  which the per-property `Apply*` methods don't check): for each
  `ShadowPropertyConfig` whose `PropertyName` doesn't match an existing
  property on the target entity, append a synthesized
  `PropertyModel(PropertyName, ClrType, IsNullable: true, MaxLength: null, IsShadow: true)`
  to that entity's `Properties` list. If the name *does* match an existing
  property (a same-named CLR property with a redundant explicit shadow-style
  `Property<T>("X")` call — technically legal but a config no-op in EF),
  skip synthesis; this is not a shadow property in that case.
- UI: `EntityNode.razor` renders shadow rows visually distinct (dimmed/
  italic style, e.g. a `.shadow-property` CSS class) and omits the
  rename/retype/remove/expand-panel affordances other property rows have —
  read-only per your answer, since there's no rewriter support to back edits.

## Testing

- `FluentConfigParserTests`: new cases per parser method (ignore property,
  ignore whole entity, unresolvable ignore argument, `[Index]`-equivalent
  fluent still works unchanged, each of the five value-generation call
  names, shadow property via string literal, shadow property name colliding
  with a real property — verifying no synthesis).
- `EntityClassParserTests`: `[Index]` single/composite/multiple-attributes/
  named-args cases.
- `ModelMergerTests`: `ApplyIgnoredProperties`, `ApplyValueGeneration`.
- `DiagramModelBuilder`-level test (in `EfSchemaVisualizer.Core.Tests` or
  wherever the existing dedupe-key/annotation-union behavior is already
  tested) for: whole-entity `Ignore<T>()` dropping an entity and its
  relationships, `[Index]`-vs-`HasIndex` fluent-wins conflict, shadow
  property synthesis end-to-end.
- `EntityNodeAccessibilityTests`-style or component test for the value-gen
  badge and shadow-row rendering, if the existing Web.Tests project has a
  precedent for asserting on `EntityNode.razor` markup (it does, per the
  aria-label test) — otherwise a markup-source assertion in the same style.

## Explicitly out of scope

- Rewriter/write-back support for any of the four (no `SetIgnore`,
  `SetValueGeneration`, `SetIndex`-from-attribute, or shadow-property
  creation/edit in `DiagramEditor`).
- `modelBuilder.Ignore<T>()` appearing *inside* a nested scope some other
  way (e.g. conditionally, inside a loop) — only the direct top-level
  statement shape is read, consistent with every other parser's
  syntax-only, non-executing approach.
- Any UI affordance for shadow properties beyond a read-only row (no
  "promote to real property" action).
