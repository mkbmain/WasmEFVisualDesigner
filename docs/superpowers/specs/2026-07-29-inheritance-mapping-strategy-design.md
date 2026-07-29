# Inheritance Mapping Strategy & Discriminator (Backlog W2 follow-up) — Design

> Addresses the backlog item **"Inheritance: `HasDiscriminator`/`HasValue`, TPT
> (`UseTptMappingStrategy`), TPC"** (`docs/backlog.md`, Priority 2), explicitly
> called out as a non-goal of the original W2 pass
> (`docs/superpowers/specs/2026-07-24-inheritance-tph-design.md`). That pass
> already folds TPH-shaped inheritance (base properties folded into derived
> entities, one `RelationshipKind.Inheritance` edge per derived entity,
> `EntityModel.BaseEntityName` / `PropertyModel.DeclaringEntityName`). This
> design adds: parsing/editing `HasDiscriminator`/`HasValue`, parsing/editing
> `UseTptMappingStrategy()`/`UseTpcMappingStrategy()`, and rendering all three
> mapping strategies (TPH/TPT/TPC) distinctly.

## Problem

Today every inheritance hierarchy is folded and rendered as if it were TPH,
regardless of what's actually configured:

- `HasDiscriminator<string>("Type").HasValue<Student>("S").HasValue<Teacher>("T")`
  fires generic `UnrecognizedConfigCall` diagnostics and is invisible/uneditable.
- `UseTptMappingStrategy()`/`UseTpcMappingStrategy()` (no-arg calls chained on
  `Entity<T>()`) are unrecognized too, and even if they were parsed, nothing
  in the model or rendering pipeline changes behavior based on them — a TPT
  hierarchy (each entity its own physical table, sharing a PK/FK) and a TPC
  hierarchy (each concrete entity its own full independent table) both
  currently render identically to TPH (one flat folded shape).

## Non-goals (explicitly out of scope for this pass)

- Removing an inheritance edge (stripping `: Person` from the class
  declaration) — unchanged from the original W2 non-goal; no rewriter support
  exists for un-inheriting.
- Synthesizing explicit `ToTable("...")` calls for every entity when
  switching to TPT/TPC. Table names shown/written continue to follow the
  existing `ToTable`-or-convention logic already in the codebase; switching
  strategy only adds/removes the single `UseTptMappingStrategy()`/
  `UseTpcMappingStrategy()` call.
- The non-generic `HasDiscriminator(Type, string)` overload, and
  `HasDiscriminator()` with no arguments (convention-named shadow column) —
  only `HasDiscriminator<T>("Name")` and `HasDiscriminator("Name")` (implicit
  `string` column type) are parsed.
- Auto-removing a user's `HasDiscriminator`/`HasValue` config when switching
  away from TPH, or auto-removing a strategy call when adding discriminator
  config to a TPT/TPC hierarchy. Both directions are **blocked** with an
  inline error instead (see Editing) — no silent deletion of user-written
  config.
- Combining owned/complex-type folding with mapping-strategy switching is
  untested territory; not specifically blocked, but not a design target.
- Diamond/multi-base edge cases beyond a single linear chain — same
  restriction as the original W2 pass.

**Known limitation:** TPT folds only the inherited KEY property into a
derived entity's own folded property list (by design — see the Folding
section below); it does not fold in other, non-key ancestor properties. If a
derived entity's own fluent config scope references an inherited non-key
property directly (e.g. `entity.HasIndex(x => x.Name)` where `Name` is
declared on the base class), that property is not present in the derived
entity's folded property list, so the reference is not correctly resolved
against the folded model. This can surface as a false model-validity
diagnostic (e.g. a `HasIndex` reference to an inherited property may read as
"missing") rather than as a real problem with the source. No code fix is
planned for this pass — it would require either re-folding non-key ancestor
properties for validation purposes only, or making the validity checker
hierarchy-aware, both larger changes than this narrow edge case warrants.

## Model changes

- New **`MappingStrategy` enum**: `Tph` (default) / `Tpt` / `Tpc`.
- **`EntityModel.MappingStrategy`**: resolved per-hierarchy and stamped onto
  *every* entity in the hierarchy (root and all descendants), so the
  strategy dropdown can render — and reflect the same value — on any card in
  the hierarchy, not just the root.
- **`EntityModel.DiscriminatorPropertyName`** / **`DiscriminatorClrType`**:
  set on the root entity only (`BaseEntityName == null` and has at least one
  descendant), from `HasDiscriminator<T>("Name")`. `DiscriminatorClrType`
  defaults to `"string"` when the non-generic overload is used.
- **`EntityModel.DiscriminatorValue`**: set on each *derived* entity from its
  own `HasValue<TDerived>(value)` call (raw literal source text, e.g.
  `"Student"` including quotes — consistent with how other literal-valued
  fields like `DefaultValueSql` are stored). `null` when not configured.
- All four fields default to values that leave every existing
  positional/object-initializer construction of `EntityModel` unaffected.
- New `RelationshipModel`/`RelationshipKind` changes: **none**. The existing
  `RelationshipKind.Inheritance` edge is still emitted unconditionally by
  `InheritanceInference.Fold`; TPC only changes whether it's *rendered*
  (see Rendering), not whether it's modeled.
- New `ModelValidity` diagnostic **`InconsistentMappingStrategyInHierarchy`**:
  fires when more than one distinct strategy call is found across the
  entities of one hierarchy (e.g. `UseTptMappingStrategy()` on the root but
  `UseTpcMappingStrategy()` on a derived entity).

## Folding: `InheritanceInference.Fold` strategy branch

Strategy is resolved once per hierarchy before folding: scan every entity in
the chain (root-first) for an explicit strategy call; the first one found
wins (root has priority); no call anywhere in the hierarchy defaults to
`Tph`. If more than one *distinct* strategy value is found among the
hierarchy's entities, emit `InconsistentMappingStrategyInHierarchy` and still
proceed using the root-priority resolution.

- **TPH / TPC**: fold all ancestor properties into the derived entity —
  today's behavior, byte-for-byte unchanged. (TPC's per-entity column shape
  is identical to TPH's; they only differ in whether the inheritance edge is
  rendered.)
- **TPT**: fold *only* the entity's inherited primary-key propert(ies)* —
  not other ancestor properties — since a TPT derived table physically has
  just its own declared columns plus the shared PK, which is also an FK back
  to the base table. Other inherited properties remain visible only on the
  ancestor's own card. The folded key property is still stamped with
  `DeclaringEntityName` = the ancestor's name, so existing property-edit
  routing (`ResolveDeclaringEntity`) needs no changes — editing the key from
  the derived card already correctly rewrites the base class.

`MappingStrategy` is stamped on every entity in the chain during this same
pass, after resolution.

## Parsing

- **`FluentConfigParser.ParseMappingStrategies`**: new extractor recognizing
  the no-arg `UseTptMappingStrategy()` / `UseTpcMappingStrategy()` calls
  chained directly on `Entity<T>()`, within the existing entity-scope walk.
  Both names added to `RecognizedCallNames`. Produces a
  `Dictionary<string /* entity name */, MappingStrategy>` covering only
  entities where a call was explicitly written (used by the strategy-
  resolution step above, not a per-entity default).
- **`FluentConfigParser.ParseDiscriminators`**: new extractor recognizing
  `HasDiscriminator<T>("Col")` / `HasDiscriminator("Col")` and each chained
  `.HasValue<TDerived>(value)` link in the same fluent chain. `HasValue`
  added to `ContextSensitiveCallNames`, scoped to `HasDiscriminator` only —
  same pattern as the existing `HasName` → `{HasKey, HasIndex}` entry, so
  `HasValue` chained onto anything else still correctly re-fires
  `UnrecognizedConfigCall`. Unreadable column-name or value arguments produce
  new `UnreadableHasDiscriminatorArgument` / `UnreadableHasValueArgument`
  diagnostics (`Parse` category).
- Both extractors run in the same parallel `Parse*` stage as the ~30 existing
  ones in `DiagramModelBuilder.Build`, and their results are merged into
  entities *before* `InheritanceInference.Fold` runs (fold needs the
  resolved strategy to decide how to fold).

## Editing (`DiagramEditor` + `OnModelCreatingRewriter`)

- **`DiagramEditor.SetMappingStrategy(entityName, strategy)`**: resolves the
  hierarchy root by walking `BaseEntityName` upward from whichever entity
  the dropdown was changed on. If the target strategy isn't `Tph` and the
  root already has discriminator config (`DiscriminatorPropertyName != null`
  or any descendant has a `DiscriminatorValue`), **rejects the edit** with an
  inline error naming the conflicting config — no silent deletion. Otherwise
  delegates to the rewriter against the root's `Entity<T>()` scope. Setting
  the strategy back to `Tph` removes whichever strategy call is present (no
  call written = convention default, matching how e.g. `RemoveDefaultValue`
  works elsewhere).
- **`DiagramEditor.SetDiscriminatorColumn(rootEntityName, columnName, clrTypeName?)`**
  / **`SetDiscriminatorValue(derivedEntityName, value)`** /
  **`RemoveDiscriminatorColumn`** / **`RemoveDiscriminatorValue`**: symmetric
  guard — rejects the edit if the resolved hierarchy strategy is `Tpt`/`Tpc`.
  `SetDiscriminatorValue` requires a discriminator column to already exist
  (validation error otherwise, since `HasValue` always chains after
  `HasDiscriminator`). Value auto-quoting follows the same rule as
  `SetDefaultValue`: plain text is auto-quoted into a string literal when the
  discriminator column's CLR type is `string` (the common/default case),
  other CLR types require an actual C# literal.
- **`OnModelCreatingRewriter.SetMappingStrategy`**: same
  find-scope → find-existing-call → mutate-or-insert-or-remove pattern as
  `SetDefaultValueSql`, operating on the root entity's `Entity<T>()` scope.
- **`OnModelCreatingRewriter.SetDiscriminatorColumn`/`SetDiscriminatorValue`/
  `RemoveDiscriminatorColumn`/`RemoveDiscriminatorValue`**: walks/builds the
  `entity.HasDiscriminator<T>("Col").HasValue<A>(x).HasValue<B>(y)` chain —
  same chained-call-building approach already used for `HasKey(...).HasName(...)`.
  Removing the column removes the whole chain (all `HasValue` links with it);
  removing a single derived type's value removes just that `.HasValue<T>(...)`
  link, keeping the rest of the chain intact.

## Rendering

- **Mapping-strategy dropdown** (TPH/TPT/TPC): rendered on every entity that
  is part of a hierarchy (has `BaseEntityName` set, or is a root with at
  least one descendant). All instances reflect and write the same
  hierarchy-wide value; changing it on any card re-syncs every card in the
  hierarchy after re-parse.
- **Discriminator summary panel**: one panel on the **root** entity's card
  only, listing the column name (editable) and every derived-type → value
  pair (each editable/removable) — same visual family as the existing
  check-constraints list (`EntityNode.razor`).
- **TPT**: derived cards show only their own declared properties plus the
  folded inherited key; other ancestor properties are visible only on the
  ancestor's card. Inheritance edge still drawn (physical PK/FK link is
  real).
- **TPC**: derived cards show fully folded properties, same as TPH.
  `DiagramSync.cs` does **not** add a `RelationshipLinkLabelModel`/link for
  `Kind == Inheritance` when the dependent entity's `MappingStrategy ==
  Tpc` — the hierarchy renders as independent tables with no connecting
  edge, since TPC has no shared physical table or FK.
- `RelationshipLinkLabel.razor`'s inheritance label is unchanged (still
  read-only "`X` extends `Y`") for TPH/TPT, and simply never renders for TPC
  members per the above.

## Error handling

- Unreadable `HasDiscriminator`/`HasValue` arguments: `Parse`-category
  diagnostics, config preserved verbatim (existing project convention for
  every other unreadable-argument case).
- Inconsistent strategy calls across one hierarchy: `ModelValidity`-category
  `InconsistentMappingStrategyInHierarchy`, root-priority resolution used
  regardless so the diagram always renders *something* consistent.
- Edit-time conflicts (strategy vs. discriminator, either direction): editor
  returns a validation failure with a message naming the conflicting config;
  no source is written when an edit is rejected — same "reject cleanly
  instead of corrupting data" precedent as `ValidateOwnedEditDepth`.

## Testing plan

- `FluentConfigParserTests`: strategy call recognition (`UseTptMappingStrategy`,
  `UseTpcMappingStrategy`), discriminator chain parsing (generic and
  non-generic `HasDiscriminator`, single and multiple chained `HasValue`),
  malformed-argument diagnostics, `HasValue` chained onto something other
  than `HasDiscriminator` still fires `UnrecognizedConfigCall`.
- `InheritanceInferenceTests`: TPT folds only the key property (other
  ancestor properties absent from the derived entity), TPH/TPC fold
  everything (unchanged), `MappingStrategy` stamped identically across every
  entity in a hierarchy, root-priority resolution when strategy is written
  on a non-root entity, `InconsistentMappingStrategyInHierarchy` fires on a
  mismatched hierarchy.
- `OnModelCreatingRewriterTests`: add/remove/switch strategy call; build a
  fresh discriminator chain; add a second `HasValue` to an existing chain;
  remove one `HasValue` link without disturbing others; remove the whole
  discriminator chain.
- `DiagramEditorTests`: `SetMappingStrategy` rejected when discriminator
  config exists (both directions); `SetDiscriminatorColumn`/`SetDiscriminatorValue`
  rejected on a Tpt/Tpc hierarchy; `SetDiscriminatorValue` rejected with no
  column configured yet; successful round-trip edits for both discriminator
  and strategy.
- `DiagramModelBuilderTests`: end-to-end TPT hierarchy (derived cards show
  only their own + key columns, edge rendered) and TPC hierarchy (derived
  cards fully folded, no edge) built from realistic source; end-to-end TPH
  hierarchy with a full discriminator chain parses, renders, and edits
  correctly.
- Existing test suite must stay green — additive fields/enum values, no
  existing assertions should need updating.
