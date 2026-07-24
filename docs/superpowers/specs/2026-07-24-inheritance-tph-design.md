# Inheritance / TPH Rendering (Backlog W2) — Design

> Addresses backlog item **W2 — Inheritance renders as unrelated fragments**
> (`docs/backlog.md`, Priority 1). Scope is the "minimum fix" described there:
> read base types from the class declaration, fold inherited properties into
> derived entities, and draw an inheritance edge — plus, per user decision,
> make folded properties fully editable from the derived card and reuse the
> existing relationship-link rendering shell for the inheritance edge.

## Problem

A TPH hierarchy (`Student : Person`, `Teacher : Person`) parses today as three
disconnected entities: `Person(Id,Name) key=[Id]`, `Student(Course) key=[]`,
`Teacher(Salary) key=[]`. Derived types don't inherit `Id`/`Name`, have no
key, and have no link to the base. `HasDiscriminator`/`HasValue` already fire
`UnrecognizedConfigCall`, but nothing indicates the three map to one table.

## Non-goals (explicitly out of scope for this pass)

- TPT (`UseTptMappingStrategy`) / TPC mapping strategies — this pass assumes
  TPH (EF's default), which is exactly "fold everything into one flat shape."
- `HasDiscriminator`/`HasValue` parsing/editing, or rendering a synthetic
  `Discriminator` shadow column.
- Removing an inheritance edge (stripping `: Person` from the class
  declaration) — no rewriter support exists for this; the reused link label
  is read-only for `Kind == Inheritance` (see Rendering).
- Diamond/multiple-interface-inheritance edge cases beyond a single linear
  base chain (C# only allows one class base anyway; interfaces in the same
  base list are simply not matched against entity names).
- Owned types (W3) — separate backlog item, unaffected by this change.

## Model changes

- `EntityModel`: add `string? BaseEntityName = null` — set only when the
  class declaration's base-list contains a type whose simple name matches
  another parsed entity's `Name` (an interface or external/unmapped base
  stays `null`).
- `PropertyModel`: add `string? DeclaringEntityName = null` — `null` means
  "declared directly on this entity's own class"; non-null (an ancestor's
  name) means the property was folded in from that ancestor via inheritance.
- `RelationshipKind`: add `Inheritance`.

All three default to values that make every existing caller/test (positional
or object-initializer construction) unaffected.

## Parsing: resolving `BaseEntityName`

`EntityClassParser.ParseEntity` currently builds each `EntityModel`
independently. Base-type resolution needs the full entity name set, so it
happens as a post-pass in `Parse` (mirroring how `ResolveKeyPropertyNames`
etc. run per-entity but dedup/cross-referencing happens after the initial
`Select(ParseEntity)`):

1. While building each entity, also capture the raw simple name text of the
   first entry in `BaseList.Types` (if the type declaration is a
   `ClassDeclarationSyntax` with a non-empty `BaseList`). A class base, when
   present, is always listed first in valid C#, so no interface-vs-class
   disambiguation is needed here.
2. After all entities are parsed and deduplicated by name, resolve: if the
   captured name matches another parsed entity's `Name`, set
   `BaseEntityName` to it; otherwise leave `null` (the first base-list entry
   was an interface, or an external/unmapped type such as `object`).

## New module: `Core.Inference.InheritanceInference`

`InheritanceInference.Fold(IReadOnlyList<EntityModel> entities) -> IReadOnlyList<EntityModel>`

Runs in `DiagramModelBuilder.Build` immediately after
`ConventionInference.InferKey` (so each entity's explicit-or-inferred key is
already settled) and before relationship inference:

1. Build a `Name -> EntityModel` map.
2. For each entity with a non-null `BaseEntityName`, walk the chain upward
   (cycle-guarded with a visited-set, since malformed input could in theory
   loop) collecting ancestor property lists root-first.
3. Derived entity's folded properties = ancestor properties (each stamped
   with `DeclaringEntityName` = the ancestor's own name) followed by the
   entity's own properties, with the entity's own property winning on a name
   collision (dropping the ancestor's copy of that name).
4. If the entity's own `KeyPropertyNames` is empty after step 3 (i.e., it had
   no explicit/inferred key of its own), inherit the nearest ancestor's
   `KeyPropertyNames` and set `IsKeyInferred = true` — reusing the existing
   W1 flag/visual, since both cases mean the same thing to the user: "this
   key is not literally written on this entity's own class."
5. Produce one `RelationshipModel` per entity-with-`BaseEntityName`:
   `PrincipalEntity = BaseEntityName`, `DependentEntity = entity.Name`,
   `Kind = RelationshipKind.Inheritance`, `PrincipalNavigation = null`,
   `DependentNavigation = null`, `ForeignKeyProperties = []`,
   `IsInferred = false` (it's explicit in source via `: Person`).

`DiagramModelBuilder.Build` concatenates these into the same `Relationships`
list as FK relationships (no separate list/field) — `DiagramSync` and the
editor UI already iterate `result.Relationships` uniformly; branching on
`Kind` is enough to change rendering/behavior.

## Editing: routing property edits to the declaring entity

Per user decision, folded properties are fully editable from the derived
card. `DiagramEditor`'s property-scoped methods (`RenameProperty`,
`ChangePropertyType`, `RemoveProperty`, `ToggleKey`, `SetColumnName`,
`SetColumnType`, `SetMaxLength`, `SetRequiredOverride`, `SetRowVersion`,
`SetConcurrencyToken`, `SetPrecision`, `SetDefaultValue`,
`SetDefaultValueSql`, `AddIndex`/index & alternate-key membership methods —
every method that currently forwards `(entityName, propertyName)` straight
into `_classRewriter`/`_configRewriter`) gain one resolution step:

```csharp
private string ResolveDeclaringEntity(string entityName, string propertyName)
{
    var property = Current.Entities
        .FirstOrDefault(e => e.Name == entityName)?.Properties
        .FirstOrDefault(p => p.Name == propertyName);
    return property?.DeclaringEntityName ?? entityName;
}
```

Each method keeps validating against the *derived* entity's folded view
(does the property exist, is it part of a key, etc. — all unchanged), but
resolves the rewriter target name once, right before the rewriter call:
`var owner = ResolveDeclaringEntity(entityName, propertyName);` then pass
`owner` instead of `entityName` to the rewriter. For a non-inherited
property `owner == entityName`, so every existing test/behavior is
unaffected.

`AddProperty` needs no change — a newly added property is always declared on
the entity it's added to, never inherited.

## Rendering

- `EntityNode.razor`: folded properties render in the derived card's normal
  property list (no separate visual treatment — matches the mental model
  that TPH physically stores everything in one table). The key marker reuses
  the existing `IsKeyInferred`-muted style regardless of whether the key was
  convention-inferred or inherited.
- `DiagramSync.cs`: a `RelationshipModel` with `Kind == Inheritance` gets a
  distinct link color (not the existing `#aaaaaa` inferred-gray, since this
  edge is explicit in source, not convention-guessed) — e.g. a solid darker
  line, visually distinguishable from both plain FK links and inferred FK
  links.
- `RelationshipLabels.For`: add `Inheritance => "▷"` (or similar short glyph)
  for the link's collapsed label.
- `RelationshipLinkLabel.razor`: when `Label.Relationship.Kind ==
  RelationshipKind.Inheritance`, expanding the label shows a read-only line
  ("`Student` extends `Person`") — no Kind dropdown, no FK checkboxes, no
  On-delete selector, no Remove button (see Non-goals: no rewriter support
  exists for un-inheriting).

## Testing plan

- `EntityClassParserTests`: `BaseEntityName` resolves for a class-base
  matching a sibling entity; stays `null` for an interface-only base list,
  an unmapped base (e.g. `Exception`), and no base list at all.
- `InheritanceInferenceTests` (new, Core.Tests): property folding (own
  property wins on name clash, `DeclaringEntityName` stamped correctly,
  multi-level chain, cycle-guarded no-op on a malformed cycle), key
  inheritance (`IsKeyInferred` set when derived has no key of its own,
  untouched when derived has its own explicit/inferred key), inheritance
  `RelationshipModel` emitted per derived entity with `Kind ==
  Inheritance`.
- `DiagramModelBuilderTests`: end-to-end TPH repro from the backlog item
  (`Person`/`Student`/`Teacher`) now renders `Student`/`Teacher` with
  `Id`/`Name` folded in, both keyed on `Id`, and an inheritance relationship
  to `Person`.
- `DiagramEditorTests`: renaming/retyping/removing a property from the
  derived entity's name when the property is actually declared on the base
  correctly rewrites the base class's source, not the derived class's
  (which has no matching member to find); a property declared directly on
  the derived entity is unaffected (still resolves to itself).
- Existing test suite must stay green — this is additive (new default-`null`
  fields, new enum value), so no existing assertions should need updating.
