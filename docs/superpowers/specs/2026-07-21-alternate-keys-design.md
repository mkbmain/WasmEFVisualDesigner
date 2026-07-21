# Alternate keys (`HasAlternateKey`) — design

> Backlog: `docs/backlog.md` Round 3 review, Priority 3, "Alternate keys unread."

## Problem

`HasAlternateKey(...)` is not parsed at all. It's dropped silently by the
parser today (and, since the recent unrecognized-call diagnostic landed,
flagged generically as `UnrecognizedConfigCall` rather than modeled). EF Core
allows **multiple** alternate keys per entity, each over one or more
properties — structurally the same shape as `HasIndex`, not `HasKey` (which
is exactly one primary key per entity).

`HasPrincipalKey` (which lets a relationship's FK target an alternate key
instead of the primary key) stays out of scope, per the existing README
"Unsupported EF Core features" list — this slice only stops alternate keys
from being silently dropped and lets them round-trip; it doesn't yet cross-
reference them from relationship parsing.

## Model

`EntityModel.AlternateKeys: IReadOnlyList<IReadOnlyList<string>>` — a list of
property-name sets, mirroring `Indexes: IReadOnlyList<IndexModel>` (list-
shaped, since an entity can have several). Defaulted to an empty list the
same way `Indexes`/`KeyPropertyNames` are.

New `EfSchemaVisualizer.Core.Merging.AlternateKeyConfig(string EntityName,
IReadOnlyList<string> PropertyNames)` record, alongside the existing
`KeyConfig`/`IndexConfig`.

## Parse

`FluentConfigParser.ParseAlternateKeys(string sourceCode) ->
ParseResult<IReadOnlyList<AlternateKeyConfig>>`, following the exact shape of
`ParseKeys`: for each `(entityName, scope)` from
`FluentSyntaxHelpers.FindConfigurationScopes`, find `HasAlternateKey` calls
via `FindCallsNamed`, read arguments via the existing
`FluentSyntaxHelpers.TryReadPropertyNameList` (already shared by `HasKey`;
handles `e => e.X`, `e => new { e.A, e.B }`, and string-param forms). Add a
new `DiagnosticCodes.UnreadableHasAlternateKeyArgument` for unreadable
arguments (same pattern as `UnreadableHasKeyArgument`).

Add `"HasAlternateKey"` to `FluentConfigParser.RecognizedCallNames` so it
stops being flagged as `UnrecognizedConfigCall`.

## Merge

`ModelMerger.ApplyAlternateKeys(EntityModel entity, IReadOnlyList<AlternateKeyConfig> configs)`
filters configs by `entity.Name` and sets `AlternateKeys` to the resulting
list of `PropertyNames` (full replace per entity — same semantics as
`ApplyKeys`/index merge, not additive). Wired into `DiagramModelBuilder.Build`
alongside the other `Apply*` calls.

## Rewrite

`OnModelCreatingRewriter.AddAlternateKey(string sourceCode, string entityName, IReadOnlyList<string> propertyNames)`
and `RemoveAlternateKey(string sourceCode, string entityName, IReadOnlyList<string> propertyNames)`,
matching an existing alternate key by property-set identity (`SequenceEqual`
over `TryReadPropertyNameList`'s result), reusing `SetIndex`'s dispatch shape
(mutate-in-place is meaningless here since there's no unique/name payload to
change — an alternate key either exists over that property set or doesn't —
so it's really just "insert into existing scope / synthesize new
`Entity<T>()` block" + "remove", not a four-case mutate/append/insert/
synthesize dispatch). `RemoveAlternateKey` throws if no match is found
(matching the "fail loudly instead of silently no-op" precedent set by the
just-shipped concurrency-token work), since a UI-driven remove always targets
something it can see.

## App (`DiagramEditor` + `EntityNode.razor`)

`DiagramEditor.AddAlternateKey(propertyName)` creates a new alternate key
containing just that property; `ToggleAlternateKeyMembership(existingKey,
propertyName, isMember)` adds/removes a property from an existing key's
property list (removing the key entirely if that empties it);
`RemoveAlternateKey(existingKey)` removes it outright. All three follow the
existing `AddIndex`/`ToggleIndexMembership`/`RemoveIndex` shape exactly.

In `EntityNode.razor`'s per-property expand panel, a new "Alternate keys:"
block directly below the existing "Indexes:" block: one row per alternate key
showing `[PropertyA, PropertyB]`, a checkbox toggling *this* property's
membership in that key, and a "×" to remove the whole key; a "+ New alternate
key on this property" button below the list. No unique/name fields (EF's
`HasAlternateKey` doesn't take either).

## Testing

- `FluentConfigParserTests`: single-property, composite (`new {}`), string-arg
  form, multiple `HasAlternateKey` calls on one entity, unreadable argument
  diagnostic, `IEntityTypeConfiguration<T>` style.
- `ModelMergerTests`: `ApplyAlternateKeys` filters by entity, multiple keys
  preserved, no configs leaves an empty list.
- `OnModelCreatingRewriterTests`: add into existing block, add via synthesize
  (no existing scope), add a second alternate key alongside an existing one,
  remove existing, remove-when-absent throws.
- `DiagramEditorTests`: add/toggle-membership/remove round-trip through
  `DiagramEditor`.
- Existing `RoundTripFuzzTests` corpus: add a `HasAlternateKey` line to the
  fixture so the no-op round-trip and rename-preserves-other-config
  assertions cover it too.

## Out of scope

- `HasPrincipalKey` / relationship cross-referencing of alternate keys.
- Naming alternate keys (EF's fluent API has no name parameter for
  `HasAlternateKey`, unlike `HasIndex`).
