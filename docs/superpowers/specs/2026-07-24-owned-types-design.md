# Owned Types Rendering (Backlog W3) — Design

> Addresses backlog item **W3 — Owned types render as their own tables**
> (`docs/backlog.md`, Priority 1). Scope, per user decision: fold `OwnsOne`
> owned properties inline into the owner's card (no separate node, no edge —
> matches EF's same-table default mapping); keep `OwnsMany` as a separate,
> visually-marked "owned" node/edge (it really is its own table with a
> shadow FK back to the owner); read-only for both; no nested-config parsing
> inside the `OwnsOne`/`OwnsMany` builder lambda this pass.

## Problem

`OwnsOne(e => e.ShippingAddress, ...)` is flagged `UnrecognizedConfigCall`,
but the owned `Address` class is still parsed by `EntityClassParser` as an
ordinary top-level class (it has no filter requiring a `DbSet<T>` or
`Entity<T>()` reference) and rendered as its own standalone table — a table
that does not exist in the database. The same is true of `OwnsMany`, except
there the owned class genuinely *is* its own table, just not an
independently-existing one.

## Non-goals (explicitly out of scope for this pass)

- `ComplexProperty` (EF7+ complex types) — a different, non-owned mapping
  concept; separate future backlog item.
- Parsing calls chained inside the `OwnsOne`/`OwnsMany` builder lambda
  (`b.Property(a => a.Street).HasMaxLength(100)`, `b.ToTable(...)`,
  `b.WithOwner(...)`) — these keep surfacing as a diagnostic rather than
  being applied (see Diagnostics).
- Editing owned properties (rename/retype/remove) — read-only this pass,
  same posture W2 took for inherited properties.
- Table splitting via `OwnsOne(...).ToTable(...)` putting the owned data in
  a different table than the default same-table mapping.
- Inheritance (W2) combined with ownership in the same chain — EF disallows
  registering an owned type as an independent entity elsewhere, so no
  conflict is expected in practice; not specifically tested.
- Nested owned types (an `OwnsOne`/`OwnsMany` call inside another owned
  type's builder lambda, e.g. `entity.OwnsOne(e => e.Address, b => {
  b.OwnsOne(a => a.Country); })`) — the builder lambda isn't a recognized
  scope boundary, so the inner call gets attributed to the *outer* entity as
  if it had called `OwnsOne`/`OwnsMany` directly. Usually harmless (the
  outer entity typically has no matching nav property, so nothing folds),
  but not guaranteed if it happens to have an unrelated same-named property
  resolving to a known entity. Documented, not fixed, this pass — see
  `ParseOwnedTypeCalls`'s XML doc in `FluentConfigParser.cs`.

## Model changes

- `PropertyModel`: add `bool IsOwned = false` and
  `string? OwnerNavigationProperty = null` — `OwnerNavigationProperty` names
  the owner's navigation property this row was folded in under (e.g.
  `"ShippingAddress"`), used purely for grouping/labeling in the UI. `null`
  for a property that isn't folded.
- `EntityModel`: add `bool IsOwned = false` — set on an `OwnsMany` target so
  the UI can mark it as "owned" (not independently removable/addable as a
  top-level entity) while still rendering it as its own card.
- `RelationshipKind`: add `Owned`.

All three default to values that leave every existing caller/test
unaffected.

## Parsing: detecting `OwnsOne`/`OwnsMany`

New extractor in `FluentConfigParser`, `ParseOwnedTypeCalls`, added to
`RecognizedCallNames` (`OwnsOne`, `OwnsMany` — so the outer call itself stops
firing `UnrecognizedConfigCall`). For each call within an entity's
config scope:

1. Read the first lambda argument's body (`e => e.ShippingAddress`) to get
   the navigation property name, same expression-shape helper already used
   for `HasForeignKey`-style nav-property lambdas elsewhere in the file.
2. Record `(OwnerEntityName, NavigationPropertyName, IsMany)` — `IsMany` is
   `true` for `OwnsMany`, `false` for `OwnsOne`. No attempt is made to parse
   the second (builder) lambda's body.

The owned class's *name* is not read from the call itself — it's resolved
from the owner's already-parsed `PropertyModel` matching
`NavigationPropertyName` (its declared CLR type, unwrapped of
`ICollection<>`/`List<>` for the `OwnsMany` case).

## New module: `Core.Inference.OwnedTypeInference`

`OwnedTypeInference.Fold(entities, ownedCalls) -> OwnedTypeFoldResult(Entities, Relationships)`

Runs in `DiagramModelBuilder.Build` immediately after `EntityClassParser`
and fluent-config merging, **before** `ConventionInference.InferKey` and
`InheritanceInference.Fold` — so a folded-away `OwnsOne` target never
acquires a spurious inferred key/relationship, and inheritance folding never
sees it.

1. Build a `Name -> EntityModel` map.
2. For each `OwnsOne` call: resolve the owner's nav property and its target
   entity. Remove the nav property from the owner's own property list;
   splice in the target entity's properties, each stamped
   `IsOwned = true`, `OwnerNavigationProperty = <nav name>`. Remove the
   target entity from the top-level entity list entirely. Recurse if the
   target itself has its own `OwnsOne` calls (owned-owns-owned chains),
   cycle-guarded with a visited-set in the same style as
   `InheritanceInference`.
3. For each `OwnsMany` call: leave both entities standalone. Set
   `IsOwned = true` on the target entity. Emit one `RelationshipModel`:
   `PrincipalEntity = owner.Name`, `DependentEntity = target.Name`,
   `Kind = RelationshipKind.Owned`, `PrincipalNavigation = navPropertyName`,
   `DependentNavigation = null`, `ForeignKeyProperties = []`,
   `IsInferred = false`.
4. If an entity ends up with zero properties and zero remaining references
   after step 2 (i.e., it existed only as an owned type), it is simply
   absent from `Entities` — no tombstone, no diagnostic; this is the
   intended fix for W3.

`DiagramModelBuilder.Build` concatenates the `Owned`-kind relationships into
the same `Relationships` list as FK/inheritance relationships — `DiagramSync`
and the editor UI already iterate `result.Relationships` uniformly.

## Rendering

- `EntityNode.razor`: folded `OwnsOne` properties render as extra rows in
  the owner's card, grouped under a muted sub-header per distinct
  `OwnerNavigationProperty` value (e.g. "ShippingAddress"), visually
  distinct from the owner's own rows and with no rename/retype/remove
  controls (read-only, matching non-goals).
- `DiagramSync.cs`: a `RelationshipModel` with `Kind == Owned` gets its own
  distinct link color/style (not reusing the inferred-gray or the
  inheritance color), so an `OwnsMany` edge reads as neither a real FK nor
  an inheritance link.
- `RelationshipLabels.For`: add `Owned => "◆"` (or similar short glyph,
  evocative of UML composition) for the link's collapsed label.
- `RelationshipLinkLabel.razor`: when `Label.Relationship.Kind ==
  RelationshipKind.Owned`, expanding shows a read-only line ("`Order` owns
  `Address` via `Addresses`") — no Kind dropdown, no FK checkboxes, no
  On-delete selector, no Remove button.
- An `IsOwned` entity's card gets a small "owned" indicator (mirroring how
  `IsKeyInferred` is shown) so the user knows it can't be removed/edited as
  an independent entity even though it has its own card.

## Diagnostics

New diagnostic code `OwnedNestedConfigIgnored`: fires once per
`OwnsOne`/`OwnsMany` call whose builder lambda contains any further method
calls, since that configuration (column names, max length, table splitting,
check constraints, etc.) is silently unapplied under this design. This
follows the project's existing convention (see W4) of flagging what's
dropped rather than staying silent. It does **not** fire for a builder
lambda with no calls in it (nothing was dropped).

## Testing plan

- `FluentConfigParserTests`: `ParseOwnedTypeCalls` resolves
  `(OwnerEntityName, NavigationPropertyName, IsMany)` for both `OwnsOne` and
  `OwnsMany`; `OwnedNestedConfigIgnored` fires only when the builder lambda
  has calls in it.
- `OwnedTypeInferenceTests` (new, Core.Tests): single-level `OwnsOne` fold
  (nav property removed from owner, target properties spliced in with
  correct stamps, target entity absent from result); multi-level owned
  chain; cycle-guarded no-op on a malformed cycle; two `OwnsOne` navs on the
  same owner targeting the same class (e.g. `ShippingAddress`/
  `BillingAddress` both typed `Address`) produce two independently-grouped
  sets of folded rows; `OwnsMany` target marked `IsOwned` and kept
  standalone with an `Owned`-kind relationship emitted.
- `DiagramModelBuilderTests`: end-to-end repro from the backlog item
  (`Order.ShippingAddress` of type `Address` via `OwnsOne`) — `Address` no
  longer appears as a standalone entity; `Order`'s card shows the folded
  properties.
- Existing test suite must stay green — this is additive (new default-value
  fields, new enum value), so no existing assertions should need updating.
