# Owned & Complex Types (Backlog Priority 2) — Design

> Addresses the Priority 2 backlog item "Owned & complex types: `OwnsOne`,
> `OwnsMany`, `ComplexProperty`" (`docs/backlog.md`). W3 (owned types
> rendering as fake standalone tables) is already fixed — this item covers
> what that pass explicitly deferred: `ComplexProperty` support, parsing the
> config chained inside an `OwnsOne`/`OwnsMany`/`ComplexProperty` builder
> lambda, and editing owned/complex properties. Nested owned-owns-owned
> attribution and table-splitting remain out of scope (see Non-goals).

## Problem

Three gaps remain after the W3 fix
(`docs/superpowers/specs/2026-07-24-owned-types-design.md`):

1. **`ComplexProperty` (EF7+ complex types) is entirely unparsed.** A
   complex-typed property renders as a bare, unrelated class with no
   indication of how EF actually maps it — the same "actively misleading"
   failure mode W3 fixed for owned types, just not yet fixed for this
   sibling concept.
2. **Config chained inside an `OwnsOne`/`OwnsMany` builder lambda is
   silently unapplied.** `b.Property(a => a.Street).HasMaxLength(100)`,
   `b.HasColumnName(...)`, etc. are flagged once via
   `OwnedNestedConfigIgnored` but never read, so the diagram doesn't reflect
   real per-property configuration a DBA would expect to see.
3. **Owned/complex-folded properties are fully read-only.** Every other
   property on the diagram supports rename/retype/remove and fluent-attribute
   edits; folded properties support none of it.

## Non-goals (explicitly out of scope for this pass)

- Complex type **collections** (`ComplexProperty(e => e.Tags)` where `Tags`
  is `IReadOnlyList<T>`). EF's relational story for these is JSON-only and
  not a good fit for the diagram/table model this app renders; a
  collection-typed `ComplexProperty` call is detected and flagged
  (`ComplexPropertyCollectionUnsupported`) rather than folded incorrectly.
- Table splitting via `OwnsOne(...).ToTable(...)` / `ComplexProperty`'s
  `ToTable` inside the builder — same non-goal W3 already carried, still
  applies now that the builder lambda is otherwise parsed.
- `WithOwner(...)` customization inside the builder lambda.
- Nested owned/complex types (an `OwnsOne`/`ComplexProperty` call inside
  another owned/complex type's own builder lambda) — same documented
  limitation W3 carried forward, not fixed here.
- Inheritance combined with ownership/complex-typing in the same chain —
  same as W3's posture.

## Phasing

Three ordered, independently shippable phases. Each should land as its own
commit/checkpoint.

### Phase 1 — `ComplexProperty` parsing, model, rendering

**Model.** Replace `PropertyModel.IsOwned` (bool) with `PropertyModel.FoldKind`,
an enum: `None` (default) / `Owned` / `Complex`. `OwnerNavigationProperty`
is unchanged in meaning and populated whenever `FoldKind != None`.
`EntityModel.IsOwned` (marks an `OwnsMany` target as a standalone-but-owned
card) is untouched — it answers a different question than `FoldKind` and has
no complex-type analog, since complex properties never stay standalone.

This is a rename with no behavior change for existing `Owned` callers;
update all read sites: `EntityNode.razor`, `DiagramSync.cs`, and existing
`IsOwned`-keyed tests (`OwnedTypeInferenceTests`,
`OwnedTypeModelFieldsTests`, `DiagramModelBuilderOwnedTypeTests`).

**Parsing.** Add `ComplexProperty` to `FluentConfigParser.RecognizedCallNames`.
New `FluentConfigParser.ParseComplexPropertyCalls`, structurally identical to
`ParseOwnedTypeCalls` minus the `IsMany` dimension: reads the first lambda
argument to resolve `(OwnerEntityName, NavigationPropertyName)`. If the
resolved nav property's declared type is a collection
(`ICollection<>`/`List<>`/`IReadOnlyList<>`/array), do not fold it; emit
`ComplexPropertyCollectionUnsupported` and leave the property/entity as
today (rendered as a plain scalar/class reference, unchanged).

**Inference.** Generalize the owned-type fold into a shared module with two
paths: the existing `OwnsOne`/`OwnsMany` path (unchanged behavior other than
the `FoldKind` rename), and a new `ComplexProperty` path — always folds
(never left standalone, no `OwnsMany`-style analog), never emits a
`RelationshipModel` (a complex type is never its own table, so there's
nothing to draw an edge to). Reuses the existing splice/cycle-guard logic
that already exists for the owned-chain case. Whether this is one file with
two entry points or two sibling files is an implementation-plan detail.

**Rendering.** `EntityNode.razor` groups `FoldKind == Complex` rows under a
sub-header the same way `FoldKind == Owned` rows are grouped today, but with
a visually distinct marker/color from the owned "◆" styling, so a user can
tell at a glance which EF concept produced a given group. No standalone
card, no relationship edge, no owned-style entity indicator — complex
properties never have independent identity.

### Phase 2 — Nested builder-lambda config parsing

Applies uniformly to `OwnsOne`, `OwnsMany`, and `ComplexProperty` builder
lambdas.

**Key mechanism.** A builder lambda body (`b => { b.Property(a => a.Street)
.HasMaxLength(100); }`) is structurally just another configuration scope.
`DiagramModelBuilder.Build`'s pipeline already merges parsed config onto
entities *before* the owned/complex fold step runs. So: if parsing discovers
this builder-lambda body and yields it as a scope keyed by the *target
type's name* (resolved the same way the outer call's nav-property-to-type
resolution already works), it can be added to the same scope list
`FluentSyntaxHelpers.FindConfigurationScopes` produces for `Entity<T>()` —
every existing per-property extractor (`HasMaxLength`, `HasColumnName`,
`IsRequired`, `HasDefaultValue`, etc.) and `ModelMerger` application then
picks it up **with no new extractor code**, because it applies to the
owned/complex type's own (pre-fold) `EntityModel` exactly like a normal
entity's config would. Only the scope-*discovery* is new.

Calls that aren't meaningful this way — `ToTable`, `WithOwner` — stay
recognized-but-ignored (existing `OwnedNestedConfigIgnored`, plus a new
`ComplexNestedConfigIgnored` for the `ComplexProperty` case) rather than
applied. Both diagnostics narrow their meaning as part of this phase: today
`OwnedNestedConfigIgnored` fires for *any* call in the builder lambda; after
Phase 2 it fires only for the still-unhandled `ToTable`/`WithOwner` calls,
since everything else is now genuinely parsed. Anything else unrecognized
still falls through to the existing `UnrecognizedConfigCall`, same as any
other unrecognized call.

### Phase 3 — Editing owned/complex properties

**Structural edits** (rename/retype/remove a folded property). Extend the
`DeclaringEntityName`-style routing `DiagramEditor` already uses for
inherited properties (W2) to also resolve a folded property's true
declaring type via its fold origin, and write to that type's own class
file. No new rewriter primitive — this is the same mechanism W2 already
built, applied to a second source of "this property isn't declared where it
appears."

**Fluent-attribute edits** (`HasColumnName`, `HasMaxLength`,
`IsRequired`, etc. on a folded property). New
`OnModelCreatingRewriter.FindOrCreateOwnedConfigScope(root, ownerEntityName,
navPropertyName)`, mirroring `FindOrCreateEntityScope`/`InsertEntityBlock`
for `Entity<T>()`: locates the existing `OwnsOne(...)`/`OwnsMany(...)`/
`ComplexProperty(...)` call's builder-lambda block if one exists, or
synthesizes one (adds the second lambda argument to a currently-bare call)
if not. Once this returns a `SyntaxNode` scope, the existing generic
`Insert*Statement`/`MutateExisting*` mutators — already scope-agnostic,
taking a plain `SyntaxNode scope` parameter — work unchanged. This gives
folded properties the same edit surface as any other property without
duplicating per-attribute rewrite logic.

**Nav-property rename.** Renaming the *owner's* navigation property (e.g.
`Order.ShippingAddress` → `Order.DeliveryAddress`) must also patch the
lambda parameter in the outer call
(`OwnsOne(e => e.ShippingAddress, ...)` → `OwnsOne(e => e.DeliveryAddress,
...)`), not just the property declaration. Called out explicitly since it's
easy to miss — the outer call's lambda is a second reference to the same
name that the existing rename machinery doesn't currently know to look for.

## Diagnostics summary

- `ComplexPropertyCollectionUnsupported` (new, Phase 1) — a `ComplexProperty`
  call targets a collection-typed nav property; left unfolded.
- `ComplexNestedConfigIgnored` (new, Phase 2) — a `ComplexProperty` builder
  lambda contains `ToTable`/`WithOwner`.
- `OwnedNestedConfigIgnored` (existing, narrowed in Phase 2) — same, for
  `OwnsOne`/`OwnsMany` builder lambdas; now fires only for
  `ToTable`/`WithOwner` instead of any call.

## Testing plan

- **Parser:** `ParseComplexPropertyCalls` resolves singular targets and
  flags collection-typed ones; nested-scope discovery produces correctly
  keyed scopes for all three call shapes (`OwnsOne`, `OwnsMany`,
  `ComplexProperty`); narrowed ignored-diagnostic behavior (fires only for
  `ToTable`/`WithOwner`, not for now-recognized calls).
- **Inference:** complex-type fold (single-level, multi-level chain, cycle
  guard on a malformed cycle) parallel to the existing
  `OwnedTypeInferenceTests` coverage for the owned path; `FoldKind` rename
  doesn't change any existing owned-path assertions' outcomes, only the
  field read.
- **Model builder end-to-end:** a `ComplexProperty`-based repro analogous to
  the existing `Order.ShippingAddress`-via-`OwnsOne` repro test; a
  builder-lambda `HasMaxLength`/`HasColumnName` case that now shows up on the
  folded property instead of being dropped.
- **Editor/rewriter:** rename/retype/remove on a folded owned property and a
  folded complex property both round-trip into the correct originating
  source file; a fluent-attribute edit on a previously-bare
  `OwnsOne(e => e.Foo)` call (no builder lambda yet) correctly synthesizes
  one; owner nav-property rename patches the outer call's lambda parameter
  as well as the property declaration.
- Full existing suite (Core + Web) must stay green throughout — the model
  changes are additive/rename-only with a default (`FoldKind.None`) that
  matches every existing non-owned/non-complex property, so no unrelated
  test should need a fixture change.
