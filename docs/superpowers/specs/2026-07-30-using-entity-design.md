# `UsingEntity`'s nested join-entity configuration — design

> Backlog: `docs/backlog.md` Priority 2, "`UsingEntity`'s nested join-entity
> configuration."

## Problem

`RelationshipConfig`/`RelationshipModel` already capture `JoinEntityName` (the
generic type argument of `UsingEntity<T>(...)`), and the rewriter already
re-emits a bare `UsingEntity<T>()` on every write. But EF's real surface is
much bigger, and none of it is read today:

```csharp
// 1. bare — already supported
.UsingEntity<PostTag>()

// 2. single join-entity-config lambda — silently dropped on any rewrite
.UsingEntity<PostTag>(j =>
{
    j.HasKey(t => new { t.PostId, t.TagId });
    j.Property(t => t.CreatedAt).HasColumnName("created_at");
})

// 3. two per-side FK lambdas (EF's own parameter names: configureRight, configureLeft)
.UsingEntity<PostTag>(
    right => right.HasOne<Tag>().WithMany().HasForeignKey(t => t.TagId),
    left  => left.HasOne<Post>().WithMany().HasForeignKey(t => t.PostId))

// 4. two FK lambdas + one join-entity-config lambda
.UsingEntity<PostTag>(
    right => right.HasOne<Tag>().WithMany().HasForeignKey(t => t.TagId),
    left  => left.HasOne<Post>().WithMany().HasForeignKey(t => t.PostId),
    j => j.HasKey(t => new { t.PostId, t.TagId }))

// 5-8. same four shapes again, but string-named (shared-type, no CLR class)
.UsingEntity("PostTags")
.UsingEntity("PostTags", j => j.Property<int>("PostId").HasColumnName("post_id"))
.UsingEntity("PostTags",
    right => right.HasOne<Tag>().WithMany().HasForeignKey("TagId"),
    left  => left.HasOne<Post>().WithMany().HasForeignKey("PostId"))
.UsingEntity("PostTags",
    right => right.HasOne<Tag>().WithMany().HasForeignKey("TagId"),
    left  => left.HasOne<Post>().WithMany().HasForeignKey("PostId"),
    j => j.HasKey("PostId", "TagId"))
```

Two distinct gaps, confirmed by user decision to cover both:

- **Read gap.** Shapes 2, 4, 6, 8 (any lambda that configures the join entity
  itself) are never parsed, so the diagram never shows join-entity column
  renames, types, or composite keys the user already wrote. Shapes 3/4/7/8's
  per-side FK lambdas are never read either, so a many-to-many with custom
  join FK column names looks identical in the diagram to one using EF's
  defaults.
- **Write gap (data loss).** Every `SetRelationshipShape` edit deletes and
  fully rebuilds the `HasMany().WithMany().UsingEntity(...)` statement
  (`BuildRelationshipStatement`/`BuildUsingEntityCall`), and the rebuild only
  ever emits a bare `UsingEntity<T>()`. Today, editing an unrelated thing
  (e.g. toggling `OnDelete`) on a many-to-many relationship silently deletes
  any join-entity config the user wrote by hand.

## Model

### `RelationshipModel` / `RelationshipConfig`

New fields, both defaulted the same way `ForeignKeyProperties` already is:

```csharp
bool JoinEntityIsSharedType = false,           // string-name form vs. generic <T>
IReadOnlyList<string>? JoinEntityRightForeignKey = null,
IReadOnlyList<string>? JoinEntityLeftForeignKey = null,
```

"Right" and "left" reuse EF's own parameter names (`configureRight`,
`configureLeft`) rather than inventing new terminology — right configures the
join entity's relationship to the type that `WithMany` was called on (the
`DependentEntity` in this tool's existing terms), left configures its
relationship to the type `HasMany` was called on (`PrincipalEntity`). No new
field is needed for the join entity's *own* key/columns — those live on the
join entity's own `EntityModel` (`KeyPropertyNames`, `Properties`, etc.),
reached via the existing `JoinEntityName`, exactly like any other entity.

### `EntityModel`

New field:

```csharp
bool IsSharedType = false,
```

A shared-type join entity has no backing class, so it's synthesized (not
class-parsed) directly by `DiagramModelBuilder` when a string-named
`UsingEntity(...)` is found with no matching class entity. Its `Properties`
list is built from every `PropertyModel` discoverable via string-literal
references inside its config/FK lambdas: `j.Property<T>("Name")`,
`j.HasKey("A", "B")`, `.HasForeignKey("Name")`. Each such property gets
`IsShadow = true` — the existing flag for "no CLR backing," already rendered
distinctly in `EntityNode.razor`, so shared-type entities need no new
rendering concept, only population. A property mentioned only inside a
per-side FK lambda and never elsewhere still gets a `PropertyModel` entry
(type `object` when no `Property<T>()` call ever states one — same fallback
`EntityClassParser` already uses for unresolvable shadow-property types).

`EntityFileOrigins`/passthrough-file logic (`DiagramEditor`,
`ProjectArchiveWriter`) is untouched for shared-type entities: they're
skipped when building that dictionary (no source file exists to origin them
to), and their only on-disk representation is the `UsingEntity(...)` call
itself, which lives in the config file like any other relationship
statement.

## Parse

All new logic lives in `FluentConfigParser.ParseRelationshipChain`, at the
point that currently sets `usingEntityCall` and reads `joinEntityName`.

1. **Identify the call shape.** `usingEntityCall.ArgumentList.Arguments`
   gives the argument count (0/1/2/3) directly; the join-entity identity
   (generic vs. string) is read from `TryGetGenericTypeArgument` (existing)
   vs. a new `TryReadSingleStringArgument`-style check on argument 0.
2. **One-lambda shapes (2, 6).** The single lambda is a join-entity-config
   lambda. Extend `FluentSyntaxHelpers.FindOwnedAndComplexNestedScopes`'s
   pattern (currently `OwnsOne`/`OwnsMany`/`ComplexProperty`-only) with a
   parallel case for `UsingEntity`: when the call has exactly one lambda
   argument and a resolved join entity name, yield that lambda's block as a
   configuration scope keyed by the join entity name. Every existing
   per-property `Parse*` method (`ParseColumnNames`, `ParseMaxLengths`,
   `ParseKeys`, ...) then reads from it with **zero new extractor code** —
   the same reuse this project used for owned/complex builder lambdas.
3. **Two-lambda shapes (3, 7).** New extractor
   `FluentConfigParser.ParseUsingEntityForeignKeys`: for each of the two
   lambdas, walk its body for a `HasOne<T>().WithMany().HasForeignKey(...)`
   (or `.HasForeignKey("...")` string form) chain and read the FK property
   list via the existing `TryReadPropertyNameList`. First lambda →
   `JoinEntityRightForeignKey`, second → `JoinEntityLeftForeignKey`. A lambda
   present but unreadable emits new
   `DiagnosticCodes.UnreadableUsingEntityForeignKeyArgument`.
4. **Three-lambda shapes (4, 8).** Both of the above: lambdas 1–2 go through
   step 3, lambda 3 goes through step 2's scope-yielding logic.
5. **String-named shapes (5–8).** Resolve the join entity name from the
   string literal instead of the generic argument; set
   `JoinEntityIsSharedType = true`. Parsing of nested lambdas is identical to
   steps 2–4 — the scope-yielding and FK extractors don't care whether the
   entity they're keyed by is class-backed or synthesized, since both are
   just names at parse time. The synthesized `EntityModel` itself is built
   later, in `DiagramModelBuilder` (see Model section), once all
   `RelationshipConfig`s and their nested property references are known.
6. **Anything else inside a config lambda that isn't a recognized
   per-property call** already surfaces via the existing
   `ParseUnrecognizedCalls` walk, since it now walks the newly-yielded scope
   like any other — no new diagnostic plumbing needed there.

`RecognizedCallNames` already contains `UsingEntity`; no change needed there.

## Merge

`ModelMerger.ApplyRelationships`: pass through
`JoinEntityIsSharedType`/`JoinEntityRightForeignKey`/`JoinEntityLeftForeignKey`
field-for-field, same pattern as `JoinEntityName` today.

New `ModelMerger`/`DiagramModelBuilder` step,
**`SharedTypeJoinEntitySynthesis`**, run after relationship merging and
before key/convention inference (so it doesn't interfere with those passes
and so its synthesized entity is a normal input to them):

- For every merged relationship with `JoinEntityIsSharedType = true` and no
  existing entity named `JoinEntityName`, synthesize one `EntityModel` with
  `IsSharedType = true` and properties built from every property name found
  across that relationship's `JoinEntityRightForeignKey`,
  `JoinEntityLeftForeignKey`, and the join-entity's own scope-parsed
  `KeyPropertyNames`/`Properties` (already produced by the reused
  per-property parsers in step 2 above — this step's job is only to *create
  the entity to hang them on*, not to parse them again).

## Validity check

No new `ModelValidityChecker` rule is required — a shared-type entity with no
key still trips the existing `EntityHasNoKey` check (it isn't `IsOwned`, and
there's no reason to special-case it: a join table genuinely needs a key).
`IndexReferencesMissingProperty`/`DuplicateColumnName` etc. already work
against any `EntityModel`, shared-type or not, with no changes.

## Rewrite

### Preserving nested config across unrelated edits

This is the write-gap fix. `OnModelCreatingRewriter.RemoveRelationship`
currently deletes the whole `ExpressionStatement` containing the
`HasMany().WithMany().UsingEntity(...)` chain. Change:

- Before removing, if a `UsingEntity(...)` call is present in the chain,
  capture its full `ArgumentList` as-is (`ArgumentListSyntax`, unmodified
  syntax — ordinary lambdas, string literals, whatever the user wrote).
- `RemoveRelationship` returns this captured argument list alongside the
  edited source (new out param or a small result type — mirrors how other
  rewriter methods already return more than a bare string where the caller
  needs more than "did it change").
- `DiagramEditor.SetRelationshipShape` threads the captured argument list
  into `OnModelCreatingRewriter.SetRelationship`, which passes it to
  `BuildUsingEntityCall`.
- `BuildUsingEntityCall` re-attaches the captured argument list verbatim
  **unless** the specific piece of state that changed is something this spec
  now models explicitly (`JoinEntityRightForeignKey`/`LeftForeignKey` changed
  via a future editor gesture — none is added by this spec; see Out of
  scope), in which case only that piece is regenerated. Since this spec adds
  no editor gesture that changes FK/key config *inside* the lambdas, in
  practice every edit this spec ships re-attaches the captured argument list
  unchanged — the fix is entirely about ceasing to destroy it, not about
  editing it yet.
- If no `UsingEntity(...)` call existed before the edit (bare `HasMany()
  .WithMany()`, or a brand new many-to-many relationship), behavior is
  unchanged: `BuildUsingEntityCall` still emits a bare
  `UsingEntity<T>()`/`UsingEntity("Name")` when `JoinEntityName` is set.

### String-named `UsingEntity` on write

`BuildUsingEntityCall` gains a branch on `JoinEntityIsSharedType`: emit
`UsingEntity("Name")` (string-literal argument) instead of
`UsingEntity<T>()` (generic type argument) when true. This only matters for
a *newly created* many-to-many relationship targeting a shared-type join
entity — existing ones round-trip via the captured-argument-list path above.

## Edit (`DiagramEditor`)

No new editor gestures in this spec — the join entity's own properties/key
are edited exactly like any other entity's (existing `RenameProperty`,
`ChangePropertyType`, key-toggle gestures already work against any
`EntityModel`, including a synthesized shared-type one, since none of those
methods currently assume a backing class beyond routing writes to a source
file — routing for a shared-type entity's edits goes through the
`UsingEntity(...)` call captured above rather than a class file, which is
covered by the rewrite section, not new editor logic).

`SetRelationshipShape` is extended only to thread the captured
argument-list-preservation described above; its public signature and
existing FK/PK/kind parameters are unaffected.

Editing the per-side FK properties (`JoinEntityRightForeignKey`/
`LeftForeignKey`) via the UI is explicitly out of scope for this spec — see
below.

## UI (rendering only)

- `EntityNode.razor`: a shared-type entity (`IsSharedType`) renders with a
  small marker (e.g. a muted "(shared type)" label near the entity name),
  mirroring how `IsOwned`/`IsKeyInferred` already get muted visual
  treatment. Its shadow properties (`IsShadow`) already render distinctly —
  no new markup needed there.
- `RelationshipLinkLabel.razor`: for a `ManyToMany` relationship whose join
  entity has a non-empty `JoinEntityRightForeignKey`/`LeftForeignKey`, show
  them as two **read-only** lines ("Join FK to {Dependent}: ...", "Join FK to
  {Principal}: ...") — read-only because editing them is out of scope (see
  below), same treatment this project already gives other read-only-for-now
  config (e.g. lambda-pair value conversions).

## Testing

- `FluentConfigParserTests`: one test per shape (2/3/4/6/7/8 — 1 and 5 are
  already covered), covering generic and string-named identity, join-entity
  property/key extraction via the reused scope mechanism, per-side FK
  extraction, and the unreadable-FK-argument diagnostic.
- `ModelMergerTests`: new fields pass through; `SharedTypeJoinEntitySynthesis`
  produces a correct `EntityModel` (key, shadow properties, `IsSharedType`)
  for a string-named `UsingEntity` with nested config.
- `DiagramModelBuilderTests`: end-to-end shared-type synthesis ordering
  relative to key/convention inference; `EntityHasNoKey` fires for a
  shared-type join entity with no `HasKey`.
- `OnModelCreatingRewriterTests`: unrelated relationship edit (e.g.
  `OnDelete`) preserves a hand-written `UsingEntity<T>(j => ...)` lambda
  byte-for-byte; new many-to-many with a shared-type join entity writes
  `UsingEntity("Name")`; bare `UsingEntity<T>()` unaffected.
- `DiagramEditorTests`: renaming/retyping a join entity's own property
  through its (synthesized or class-backed) `EntityModel` still round-trips
  correctly now that it can be shared-type.
- `RoundTripFuzzTests` fixture: add one many-to-many relationship using each
  of shapes 2, 4, 6, 8 to the corpus, so the existing no-op round-trip and
  rename-preserves-other-config assertions cover the new preservation
  behavior.

## Docs

- README: remove `UsingEntity`'s nested configuration from the "Unsupported
  EF Core features" list; note the two remaining non-goals below.
- `docs/backlog.md`: flip the item to `- [x]` with an **Update:** paragraph
  in the file's established style.

## Out of scope

- **Editing** the join entity's per-side FK properties
  (`JoinEntityRightForeignKey`/`LeftForeignKey`) or its shared-type-ness via
  the UI — this spec reads, models, renders, and round-trip-preserves them,
  but changing them stays a hand-edit-the-source operation for now, the same
  bar `HasPrincipalKey`'s spec set for its own first pass before the UI
  existed. A future pass can add editor gestures once there's UI demand.
- Nested config beyond `HasOne/WithMany/HasForeignKey` inside a per-side FK
  lambda (e.g. `.OnDelete(...)` chained there) — flagged via the existing
  generic unrecognized-call mechanism, not silently dropped, but not parsed
  into a model field either.
- Table splitting or independent `ToTable(...)` mapping specifically for a
  shared-type join entity beyond what the existing `ToTable` parser already
  reads once such an entity exists in the model (no new parser code, but
  also not specifically tested by this spec).
- Anything to do with more than one `UsingEntity` overload family colliding
  in the same file in ways EF itself wouldn't allow (e.g. mixing generic and
  string-named forms for the same relationship) — not a real EF shape, no
  handling needed.
