# EF Convention Inference (Backlog W1) — Design

> Addresses backlog item **W1 — No EF conventions are applied**
> (`docs/backlog.md`, Priority 1). Scope is the "minimum fix" described there:
> infer `Id`/`<Type>Id` primary keys and navigation+FK-property relationships,
> render inferred items distinctly, and never persist an inferred value to
> source unless the user actively edits it.

## Problem

`EntityClassParser` and `FluentConfigParser` only read what is written
explicitly. A perfectly ordinary convention-based model (`int Id`, a
`Customer Customer` navigation + `int CustomerId` FK, no fluent config, no
data annotations) renders as disconnected, keyless boxes — even though EF
Core maps it correctly at runtime. This is the single biggest gap between
what the diagram shows and what a real, convention-based project actually is.

## Non-goals (explicitly out of scope for this pass)

- Composite/multi-column convention keys beyond the two EF recognizes
  (`Id`, `<TypeName>Id`).
- Inferring a relationship from an FK-shaped property with **no** navigation
  property present (EF's broader convention allows this; this pass requires
  both, per product decision, to keep false-positive risk low).
- Actually suppressing/removing a convention-inferred relationship from the
  model (would need `.Ignore()`/`[NotMapped]`-style semantics — separate,
  larger change).
- Inheritance conventions (W2) and owned-type conventions (W3) — separate
  backlog items, unaffected by this change.
- Any change to how navigation-typed properties currently render as columns
  (pre-existing behavior, unrelated to this fix).

## Architecture

A new pure module, `EfSchemaVisualizer.Core/Inference/ConventionInference.cs`,
with two static entry points. It is invoked from `DiagramModelBuilder.Build`
**after** all explicit fluent-config and data-annotation merging has already
produced the `entities` list and the explicit `relationshipModels`/
`mergedRelationshipConfigs`. Inference only fills gaps; it never overrides
anything explicit, and nothing it produces is written back to source except
through the existing edit gestures (see "Editing an inferred relationship"
below) — the inferred state is recomputed fresh on every parse, so it can
never go stale relative to the source.

```
DiagramModelBuilder.Build
  1. parse classes + config (existing)
  2. merge all explicit config into `entities` / explicit relationships (existing)
  3. NEW: entities = entities.Select(ConventionInference.InferKey)
  4. NEW: inferredRelationships = ConventionInference.InferRelationships(entities)
          .Where(not already covered by an explicit relationship)
  5. NEW: relationshipModels = explicit relationshipModels + inferredRelationships
```

## Model changes

- `EntityModel`: add `bool IsKeyInferred = false`.
- `RelationshipModel`: add `bool IsInferred = false`.

Both default to `false`, so every existing caller/test that constructs these
records positionally or with object initializers is unaffected.

## Primary-key inference

`ConventionInference.InferKey(EntityModel entity) -> EntityModel`

Runs only when `entity.KeyPropertyNames.Count == 0 && !entity.IsKeyless`
(i.e., no explicit `HasKey`, no `[Key]` attribute, not `HasNoKey`/`[Keyless]`).

Matches EF's real convention:
1. A property named `Id` (case-insensitive) — wins outright if present.
2. Else a property named `<TypeName>Id` (case-insensitive) where
   `TypeName` is `entity.Name`.
3. Else: no key is inferred; entity renders exactly as it does today
   (keyless-looking, `IsKeyInferred` stays `false`).

On a match, returns `entity with { KeyPropertyNames = [name], IsKeyInferred = true }`.

## Relationship inference

`ConventionInference.InferRelationships(IReadOnlyList<EntityModel> entities) -> IReadOnlyList<RelationshipModel>`

For each entity (the candidate dependent), for each of its properties whose
`ClrType` matches another entity's `Name` (a reference navigation property —
detected the same way `EntityClassParser.TryGetNavigationTargetEntity`
already does), look for a same-entity scalar property matching, in order,
case-insensitively:

1. `<NavPropertyName>Id`
2. `<PrincipalTypeName>Id` (only tried if it differs from #1)

First match wins as the FK property. If neither is found, no relationship is
inferred for that navigation property.

Given a matched nav+FK pair, relationship kind and principal-side navigation
are resolved with the same back-reference scan
`EntityClassParser.FindPrincipalBackReference` already performs on explicit
annotation relationships:
- Principal has a collection property whose element type is the dependent →
  one-to-many, `PrincipalNavigation` = that property's name.
- Principal has a single reference property of the dependent's type → one-to-one,
  `PrincipalNavigation` = that property's name.
- Neither → one-to-many, `PrincipalNavigation = null` (EF's own default).

Produces a `RelationshipModel` with `IsInferred = true`,
`ForeignKeyProperties = [fkPropertyName]`.

**Dedup against explicit relationships:** an inferred relationship is dropped
if any explicit (fluent or attribute-derived) relationship already exists
with the same `(DependentEntity, ForeignKeyProperties)` — that FK column set
is already spoken for, so convention does not get a second say. This mirrors
the existing fluent-vs-annotation dedup already in `DiagramModelBuilder`.

## Rendering

- `EntityNode.razor`: the primary-key marker for a property listed in
  `entity.KeyPropertyNames` renders muted/dashed instead of solid when
  `entity.IsKeyInferred` is true (same marker glyph, different style — no new
  UI element).
- `DiagramSync.cs`: the `LinkModel` created for a relationship gets a dashed
  stroke style when `relationship.IsInferred` is true, otherwise unchanged
  (solid, current behavior).

Neither the SVG/Mermaid exporters nor the property-column list change in this
pass — out of scope (see Non-goals).

## Editing an inferred relationship

`DiagramEditor.SetRelationshipShape` currently always calls
`_configRewriter.RemoveRelationship(...)` first and returns
`DiagramEditResult.Fail("Could not locate this relationship's existing
configuration to update.")` if that returns the source unchanged. For an
inferred relationship there is, by definition, nothing in source to remove
yet, so every attempt to change its kind/FK/on-delete behavior through the
existing UI would fail.

Fix: when `relationship.IsInferred` is true, skip the removal step entirely
and go straight to `_configRewriter.SetRelationship(...)`, which already
handles "insert brand-new config" correctly. This is the concrete mechanism
behind "never write an inferred value back to source unless the user edits
it" — the first edit is what materializes it into real fluent config; every
render before that stays derived-only.

`DiagramEditor.RemoveRelationship` on a still-inferred relationship keeps
failing (nothing to remove), but gets a clearer message: "This relationship
is inferred from naming convention and isn't backed by explicit
configuration yet — change its kind or foreign key first to make it
explicit." Actually suppressing a convention relationship is out of scope
(see Non-goals).

Primary-key toggling (`DiagramEditor.ToggleKeyMembership`) needs no code
change: `OnModelCreatingRewriter.SetKey` already handles the "no existing
`HasKey` call" case by inserting one, so editing an inferred key already
materializes correctly. Covered by a regression test rather than a code
change.

## Testing plan

- `ConventionInferenceTests` (new, Core.Tests): key inference (`Id` wins over
  `<Type>Id`, case-insensitivity, no-op when explicit key/keyless present,
  no-op when neither pattern matches); relationship inference (nav+FK pair →
  one-to-many, one-to-one via back-reference, self-referencing entity, dedup
  against an explicit relationship on the same FK, no inference without a
  matching FK property, no inference without a nav property).
- `DiagramModelBuilderTests` (existing file): end-to-end convention-only model
  (the exact repro from the backlog item) now renders two keyed, related
  entities with `IsKeyInferred`/`IsInferred` set; explicit config still wins
  over convention in a mixed model.
- `DiagramEditorTests` (existing file): editing kind/FK on an inferred
  relationship succeeds and materializes explicit fluent config;
  `ToggleKeyMembership` on an inferred key succeeds and materializes an
  explicit `HasKey`.
- Existing test suite must stay green — this is additive (new default-`false`
  flags), so no existing assertions should need updating.
