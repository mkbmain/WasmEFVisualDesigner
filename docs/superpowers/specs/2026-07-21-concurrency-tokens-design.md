# Concurrency tokens — design

> Addresses the Round 3 Priority 3 backlog item in `docs/backlog.md`:
> "Concurrency tokens unread." Covers `.IsRowVersion()`, `.IsConcurrencyToken()`,
> `[Timestamp]`, and `[ConcurrencyCheck]`.

## Goal

EF Core lets a property act as an optimistic-concurrency token via the fluent
`.IsRowVersion()` / `.IsConcurrencyToken()` calls, or the equivalent data
annotations `[Timestamp]` (row version) / `[ConcurrencyCheck]` (concurrency
token). None of the four are read today — they're silently dropped from the
model (and, for the fluent calls, flagged generically by
`UnrecognizedConfigCall` since they aren't in `RecognizedCallNames`). This
pass parses, merges, displays, and — unlike the previous badge-only
(`ValueGenerated`) pass — makes both flags fully editable in the diagram,
since the user asked for write-back this round.

`IsRowVersion()` and `[Timestamp]` are semantically a stricter case of
concurrency checking, but at the syntax layer the four constructs are
independent: a property can carry `.IsRowVersion()` and `.IsConcurrencyToken()`
simultaneously (redundant in EF but not invalid), so the model represents them
as two independent booleans rather than one collapsed mode.

## Model

`PropertyModel` (`src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`) gains
two new optional-positional fields, appended after `IsShadow`:

- `bool IsRowVersion = false`
- `bool IsConcurrencyToken = false`

## Parsing — fluent side

New DTO in `src/EfSchemaVisualizer.Core/Merging/`:

```csharp
public sealed record ConcurrencyTokenConfig(
    string EntityName, string PropertyName, bool IsRowVersion, bool IsConcurrencyToken);
```

New `FluentConfigParser.ParseConcurrencyTokens(string sourceCode)` method,
returning `IReadOnlyList<ConcurrencyTokenConfig>` (no `ParseResult` wrapper —
both calls are bare, no arguments to misparse, same reasoning as
`ParseIgnoredEntities`/`ParseKeylessEntities`). Within each
`FluentSyntaxHelpers.FindConfigurationScopes` scope:

- `FindCallsNamed(scope, "IsRowVersion")` → resolve property via
  `GetPropertyNameFor`, emit `ConcurrencyTokenConfig(entity, property, IsRowVersion: true, IsConcurrencyToken: false)`.
- `FindCallsNamed(scope, "IsConcurrencyToken")` → same, emit
  `ConcurrencyTokenConfig(entity, property, IsRowVersion: false, IsConcurrencyToken: true)`.

Both lists are merged per-property in `ModelMerger` (below), so a property
with both calls present ends up with both flags `true` — the two loops don't
need to coordinate with each other. Missing property name emits
`UnresolvablePropertyName`, matching every other parser.

`"IsRowVersion"` and `"IsConcurrencyToken"` are added to
`FluentConfigParser.RecognizedCallNames` so they stop being flagged by
`UnrecognizedConfigCall`.

## Parsing — attribute side

`EntityClassParser.ParseProperty` gets two new bare-presence checks, same
one-line shape as the existing `[Required]` check:

```csharp
bool isRowVersionAttr = FindAttribute(attributeLists, "Timestamp") is not null;
bool isConcurrencyTokenAttr = FindAttribute(attributeLists, "ConcurrencyCheck") is not null;
```

threaded into the `PropertyModel` constructor call as the initial value of
`IsRowVersion`/`IsConcurrencyToken` (later overwritten by fluent config in
`ModelMerger`, per the fluent-wins precedent every other dual-source scalar
field already follows).

No new diagnostic codes: neither call nor attribute takes an argument, so
there's no "unreadable argument" case to cover.

## Merging

`ModelMerger` gains `ApplyConcurrencyTokens(EntityModel entity, IReadOnlyList<ConcurrencyTokenConfig> configs)`,
same `IndexByProperty` shape as `ApplyValueGeneration`, except a property may
appear in the config list twice (once from each fluent call) — the merge
folds by OR-ing flags per field rather than taking a single `FirstOrDefault`:
for each property, if a `IsRowVersion: true` config exists for it, set
`property.IsRowVersion = true`; independently, if a `IsConcurrencyToken: true`
config exists, set `property.IsConcurrencyToken = true`. Attribute-derived
`true` values already sit on the incoming `EntityModel.Properties` and are
never downgraded to `false` by an absent fluent config (fluent only ever
raises, never lowers, matching the "fluent wins on conflict" framing used
elsewhere — here there's no real conflict since both sources only assert
`true`).

Registered in `DiagramModelBuilder.Build` alongside the other `Apply*` calls.

## Rewriter (`OnModelCreatingRewriter`)

Two independent Set/Remove pairs, each following `SetKeyless`'s bare-marker
template but chained onto the property's own `.Property(e => e.Foo)` call
(the way `RewriteIsRequired`'s append branch does) rather than onto the
entity receiver:

- `SetRowVersion(sourceCode, entityName, propertyName)` /
  `RemoveRowVersion(sourceCode, entityName, propertyName)`
- `SetConcurrencyToken(sourceCode, entityName, propertyName)` /
  `RemoveConcurrencyToken(sourceCode, entityName, propertyName)`

Each `Set*` resolves the property's existing `.Property(...)` call within the
entity's scope (reusing the same property-call-resolution helpers
`RewriteIsRequired`'s append case uses); if the target bare call
(`.IsRowVersion()` / `.IsConcurrencyToken()`) is already chained on, no-op
(idempotent); otherwise append it to the existing property-call chain, or
synthesize a new `entity.Property(e => e.Foo).IsRowVersion();`-shaped
statement if the property has no config statement yet at all (mirrors
`RewriteIsRequired`'s 4-branch cascade: mutate n/a here since there's no
argument to mutate — only append / insert-statement / synthesize-block
apply). `Remove*` locates the chained call and unwraps it back to its
receiver, same mechanics as `RemoveKeyless`.

The two pairs are independent of each other — setting `IsRowVersion` does not
touch `IsConcurrencyToken` or vice versa, since EF permits both simultaneously
and this codebase doesn't invent invariants EF itself doesn't enforce (same
reasoning as "Table vs. View are not cross-cleared" in the keyless/view
design).

## DiagramEditor (`src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`)

Two new methods, following `SetKeyless`'s shape (no-op check against the
property's current flag, then call the matching rewriter `Set*`/`Remove*`,
then `Apply`):

- `SetRowVersion(string entityName, string propertyName, bool isRowVersion)`
- `SetConcurrencyToken(string entityName, string propertyName, bool isConcurrencyToken)`

## UI (`Diagram/EntityNode.razor`)

In the property expand panel, alongside the existing Required-override
`<select>`: two independent checkboxes, "Row version" and "Concurrency
token", each wired directly to `SetRowVersion`/`SetConcurrencyToken` on
toggle (no intermediate commit button, matching the index-membership
checkbox pattern).

## Testing

Same shape as the keyless/view precedent (this pass also includes
write-back):

- `FluentConfigParserTests`: `ParseConcurrencyTokens` — `IsRowVersion` only,
  `IsConcurrencyToken` only, both present on the same property, unresolvable
  property name.
- `EntityClassParserTests`: `[Timestamp]` and `[ConcurrencyCheck]` attribute
  detection, independently and together.
- `ModelMergerTests`: `ApplyConcurrencyTokens` — each flag independently,
  both together, attribute-seeded value preserved when no fluent config
  exists for that property.
- `OnModelCreatingRewriterTests`: append / insert-statement / synthesize-block
  cases for both `SetRowVersion` and `SetConcurrencyToken`, their `Remove*`
  counterparts, and independence (setting one doesn't disturb the other when
  both are present).
- `DiagramEditorTests`: `SetRowVersion`/`SetConcurrencyToken`, including
  their no-op paths.
- A markup-source test (matching `EntityNodeAccessibilityTests`'s style) for
  the two new checkboxes' presence and labeling.

## Explicitly out of scope

- Any validation or warning when a property carries both `[Timestamp]` and an
  explicit `[ConcurrencyCheck]`, or a CLR type EF wouldn't normally accept for
  a row-version column (e.g. `IsRowVersion()` on a non-`byte[]` property) —
  not validated, matching this codebase's existing precedent of not
  validating other contradictory/unusual combinations (e.g. `HasPrecision` on
  a non-decimal property).
- Auto-deriving `IsConcurrencyToken: true` display whenever `IsRowVersion` is
  true (even though EF's own semantics imply it) — the two badges/checkboxes
  reflect the raw syntax present, not EF's derived runtime behavior, matching
  how `Schema` reuse was avoided for anything that would blur two distinct
  EF concepts together in the keyless/view design.
