# `HasPrincipalKey` support — design

> Backlog: `docs/backlog.md` Priority 2, "`HasPrincipalKey`."

## Problem

`HasPrincipalKey(...)` — which points a relationship's foreign key at a
non-primary-key principal property, typically an alternate key — isn't parsed
at all today. It's silently dropped (or, since the `UnrecognizedConfigCall`
work landed, flagged generically) rather than modeled. This has been called
out as out of scope in two prior specs
(`2026-07-12-has-relationships-config-design.md`,
`2026-07-21-alternate-keys-design.md`) pending the diagram/UI existing to
clarify what "editing a relationship" needs. Both now exist, so this slice
adds full read/write support, following the exact precedent set by
`ForeignKeyProperties`/`HasForeignKey`.

**A note on validation scope.** It's tempting to validate that
`HasPrincipalKey`'s properties match a declared key (the principal's PK or
one of its `HasAlternateKey` sets) — mirroring the existing "FK targets a
keyless principal" check. That would be wrong: in real EF, calling
`HasPrincipalKey` on properties that aren't already a key **implicitly
creates an alternate key** for you at model-build time; no separate
`HasAlternateKey` call is required. Flagging that shape as invalid would be a
false positive on the single most common usage. The one thing that
genuinely breaks the model is `HasPrincipalKey` naming a property that no
longer exists on the principal at all (stale after a rename/removal) — the
same failure mode `ModelValidityChecker.CheckIndexReferencesMissingProperty`
already catches for indexes. This spec adds the equivalent check for
principal keys instead of a "must match a declared key" check.

## Model

`RelationshipConfig` (`EfSchemaVisualizer.Core.Merging`) and `RelationshipModel`
(`EfSchemaVisualizer.Core.Model`) each gain:

```csharp
IReadOnlyList<string>? PrincipalKeyProperties = null
```

Defaulted to an empty list in the same `init`-backed-property style
`ForeignKeyProperties` already uses on both records.

## Parse

`FluentConfigParser`:

- Add `"HasPrincipalKey"` to `RecognizedCallNames` (alongside `HasForeignKey`,
  `OnDelete`, etc.) so it's no longer caught by `ParseUnrecognizedCalls`.
- In `ParseRelationshipChain`'s `FluentSyntaxHelpers.WalkChainedTail` switch,
  capture a `HasPrincipalKey` invocation the same way `HasForeignKey` is
  captured today (new local `hasPrincipalKeyCall`, new `case
  "HasPrincipalKey":`).
- Read its argument(s) via the existing
  `FluentSyntaxHelpers.TryReadPropertyNameList` — the same helper already
  shared by `HasForeignKey`, `HasKey`, and `HasAlternateKey`, so single
  lambda-member, `new { }` composite, and string-param forms all work for
  free.
- If the call is present but unreadable, emit a new
  `DiagnosticCodes.UnreadableHasPrincipalKeyArgument`, worded and shaped
  identically to `UnreadableHasForeignKeyArgument`.
- Populate `RelationshipConfig.PrincipalKeyProperties` from the read list
  (empty list when no `HasPrincipalKey` call is present, matching
  `ForeignKeyProperties`'s default-to-empty behavior).

Call order in the chain is not assumed — `HasPrincipalKey` can appear before
or after `HasForeignKey`/`OnDelete`/`HasConstraintName`, exactly like those
calls already tolerate each other's ordering.

## Merge

`ModelMerger.ApplyRelationships`: pass `c.PrincipalKeyProperties` straight
through into the `RelationshipModel` constructor call, field-for-field,
alongside the existing `ForeignKeyProperties` passthrough.

## Validity check

`ModelValidityChecker`: new `CheckPrincipalKeyReferencesMissingProperty`,
run per-relationship alongside `CheckForeignKeyTargetsKeylessPrincipal`.

- Skip when `PrincipalKeyProperties.Count == 0`, or the relationship kind is
  `Inheritance`/`Owned` (same guards `CheckForeignKeyTargetsKeylessPrincipal`
  uses).
- Look up the principal entity; if any name in `PrincipalKeyProperties` isn't
  in the principal's current property list, emit a new
  `DiagnosticCodes.PrincipalKeyReferencesMissingProperty` diagnostic, worded
  and shaped identically to `IndexReferencesMissingProperty`:
  `"Relationship from '{DependentEntity}' references principal key
  propert{y/ies} '{missingList}' on '{PrincipalEntity}', which no longer
  exist on the entity."`

## Rewrite

`OnModelCreatingRewriter`:

- New `AppendHasPrincipalKey(ExpressionSyntax chain, IReadOnlyList<string>
  principalKeyProperties, string? principalGeneric)`, structurally identical
  to `AppendHasForeignKey` (same lambda-body-vs-anonymous-object branching
  for single vs. composite key, same early-return when the list is empty).
- Called immediately after `AppendHasForeignKey` in both the `OneToOne` and
  `OneToMany` branches of `BuildRelationshipStatement`, before `AppendOnDelete`.
- For `OneToOne`, pass `relationship.PrincipalEntity` as `principalGeneric`
  (mirrors how `AppendHasForeignKey` is called with `relationship
  .DependentEntity` there, for the same generic-overload disambiguation
  reason); pass `null` for `OneToMany`.

## Edit (`DiagramEditor`)

`SetRelationshipShape` gains a new parameter,
`IReadOnlyList<string> newPrincipalKeyProperties`:

- Included in the existing no-op short-circuit comparison
  (`SequenceEqual` against `relationship.PrincipalKeyProperties`).
- Validated against the **principal** entity's current properties (new
  check, parallel to the existing dependent-property check already run for
  `newForeignKeyProperties`); on a missing property, fail with `"'{name}'
  is not a property of '{PrincipalEntity}'."`
- Included in the `with` update passed to the rewriter.
- Many-to-many still rejects any non-empty foreign key list; the same
  rejection extends to a non-empty principal key list ("Many-to-many
  relationships cannot have a foreign key." stays the guarding message,
  since principal key without a foreign key is meaningless).

`AddRelationship` is unaffected — new relationships start with an empty
`PrincipalKeyProperties`, same as today's empty `ForeignKeyProperties`.

## UI (`RelationshipLinkLabel.razor`)

- New "Principal key" checkbox block, placed after the existing "Foreign
  key" block and before "On delete", same markup pattern (checkbox per
  property, `@onchange` toggling membership in a local `List<string>`,
  `stopPropagation` on pointer/mouse events).
- Lists the **principal** entity's properties (new `PrincipalProperties`
  computed property, mirroring the existing `DependentProperties`), not the
  dependent's.
- Shown under the same `_kind != RelationshipKind.ManyToMany` guard as the
  foreign-key block.
- New `_principalKeyProperties` field, seeded in `ToggleExpand` and included
  in the `Commit` call to `SetRelationshipShape`.

## Docs

- README: remove `HasPrincipalKey` from the "Unsupported EF Core features"
  list.
- `docs/backlog.md`: flip the `HasPrincipalKey` item to `- [x]` and append an
  `**Update:**` paragraph in the file's established style, naming the new
  model field, parser/rewriter/`DiagramEditor` methods, and UI location.

## Testing

- `FluentConfigParserTests`: single-property, composite (`new {}`), unreadable
  argument diagnostic, `HasPrincipalKey` interleaved before/after
  `HasForeignKey`/`OnDelete` in the chain.
- `ModelMergerTests`: `PrincipalKeyProperties` passthrough, default-empty
  when absent.
- `ModelValidityCheckerTests`: missing-property-reference case fires;
  valid case (properties exist, whether or not they form a declared key)
  does not fire.
- `OnModelCreatingRewriterTests`: write for `OneToOne` (with generic arg) and
  `OneToMany` (without), no-op when empty, round-trip alongside existing
  `HasForeignKey`/`OnDelete`/`HasConstraintName` calls.
- `DiagramEditorTests`: set/clear principal key properties, rejection when a
  named property doesn't exist on the principal, many-to-many rejection.
- `RoundTripFuzzTests` fixture: add a `HasPrincipalKey` line to the corpus so
  the no-op round-trip and rename-preserves-other-config assertions cover it.

## Out of scope

- Auto-inferring or suggesting which properties form a "natural" alternate
  key — the user picks properties explicitly, same as `ForeignKeyProperties`.
- Cross-referencing against `EntityModel.AlternateKeys` in any way (see the
  validation-scope note above — this is deliberate, not a gap).
- `UsingEntity`'s nested join-entity configuration — separate backlog item,
  unaffected by this change.
