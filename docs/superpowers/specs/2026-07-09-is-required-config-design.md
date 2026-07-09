# `IsRequired` / Nullability Fluent Config — Design

**Status:** approved, ready for planning
**Backlog item:** Priority 2 — `[spec/plan] IsRequired / nullability as fluent config (distinct from CLR ?)`

## Problem

`PropertyModel.IsNullable` today reflects only the CLR type (`string?` vs `string`).
EF Core lets a fluent `.IsRequired()` / `.IsRequired(false)` call override that at the
mapping level, independent of the CLR type. Neither the parser, the merger, nor the
rewriter know about this call today. This item adds it, following the same
parse → merge → rewrite pattern already established for `HasMaxLength`.

## Model changes

`PropertyModel` gains one new field:

```csharp
public sealed record PropertyModel(
    string Name,
    string ClrType,
    bool IsNullable,
    int? MaxLength,
    bool? IsRequiredOverride);
```

- `IsRequiredOverride` is `null` when no fluent `.IsRequired(...)` call configures the
  property — the CLR-derived `IsNullable` is authoritative.
- `IsRequiredOverride` is `true`/`false` when a fluent call was found, taking
  precedence over `IsNullable` for display/round-trip purposes. The two fields are
  kept distinct (not collapsed) so a UI can show "CLR: nullable, but fluent API
  requires it" type disagreements later — this mirrors the backlog's explicit call-out
  that these are separate concerns.
- Existing `EntityClassParser` call sites pass `IsRequiredOverride: null`; it is only
  ever populated by `ModelMerger`.

## Parsing: `FluentConfigParser.ParseIsRequired`

New method, same shape as `ParseMaxLengths`:

```csharp
public ParseResult<IReadOnlyList<IsRequiredConfig>> ParseIsRequired(string sourceCode)
```

New DTO:

```csharp
public sealed record IsRequiredConfig(string EntityName, string PropertyName, bool IsRequired);
```

Walk each `Entity<T>` config invocation, find calls named `IsRequired`, resolve the
property name via the existing `FluentSyntaxHelpers.GetPropertyNameFor`. Argument
handling:

- No arguments (`.IsRequired()`) → `IsRequired = true`.
- One boolean literal argument (`.IsRequired(false)` / `.IsRequired(true)`) → that
  literal's value.
- One non-literal argument (`.IsRequired(someFlag)`) → emit a new diagnostic code
  `UnreadableIsRequiredArgument` (same treatment as `UnreadableMaxLengthArgument`) and
  skip, consistent with the "surface via diagnostics, don't silently drop" rule from
  Priority 0.
- Unresolvable property name → existing `UnresolvablePropertyName` diagnostic, same as
  today.

## Merging: `ModelMerger.ApplyIsRequired`

```csharp
public static EntityModel ApplyIsRequired(EntityModel entity, IReadOnlyList<IsRequiredConfig> configs)
```

Mirrors `ApplyMaxLengths`: for each property, look up a matching `(EntityName,
PropertyName)` config and set `IsRequiredOverride` accordingly; properties with no
match keep `IsRequiredOverride: null`. `ApplyMaxLengths` and `ApplyIsRequired` are
separate composed calls (consistent with how `AddProperty`/`RewriteMaxLength` and the
rename/add/drop-property methods are already composed rather than orchestrated) —
callers who want both call both.

## Rewriting: `OnModelCreatingRewriter`

Two new public methods mirroring `RewriteMaxLength` / `RemoveMaxLength` exactly,
reusing the existing private helpers (`FindEntityConfigInvocations`, `FindCallsNamed`,
`GetPropertyNameFor`, `GetPropertyNameForPropertyCall`, `GetPropertyLambdaParameterName`,
`FindOnModelCreatingMethod`, `BuildEntityInvocationStatement`) with new
`IsRequired`-specific leaf builders alongside the existing `HasMaxLength` ones.

```csharp
public string RewriteIsRequired(string sourceCode, string entityName, string propertyName, bool newIsRequired)
```

Same four-case dispatch as `RewriteMaxLength`:

1. **Mutate existing `.IsRequired(...)` call.** Replace its argument list: empty
   (bare `.IsRequired()`) when `newIsRequired` is `true`, a single `false` literal
   argument when `newIsRequired` is `false`. (An existing bare `.IsRequired()` being
   "mutated" to `true` is a no-op replacement — harmless, kept simple rather than
   special-cased.)
2. **Append to an existing bare `.Property(...)` call with no `.IsRequired(...)`
   chained.** Same shape as `AppendMaxLengthToPropertyCall`, appending
   `.IsRequired()` or `.IsRequired(false)`.
3. **Insert a new `entity.Property(e => e.X)...` statement** into an existing
   `Entity<T>(entity => { ... })` block, same shape as `InsertPropertyStatement`.
4. **Synthesize a whole new `modelBuilder.Entity<T>(entity => { ... })` block**
   when the entity isn't configured at all yet, same shape as `InsertEntityBlock`.

Codegen style (confirmed): `true` is emitted as bare `.IsRequired()`; `false` is
always emitted as explicit `.IsRequired(false)` (there is no bare form for false).

```csharp
public string RemoveIsRequired(string sourceCode, string entityName, string propertyName)
```

Mirrors `RemoveMaxLength`: finds the matching `.IsRequired(...)` call and replaces it
with its receiver expression (the bare `Property(...)` call), leaving the property
call itself in place. No-op (returns `sourceCode` unchanged) if no such call exists.

## Testing

New test files/sections mirroring the existing `HasMaxLength` coverage:

- `FluentConfigParserTests`: bare `.IsRequired()`, `.IsRequired(false)`,
  `.IsRequired(true)`, non-literal argument → diagnostic, unresolvable property name
  → diagnostic, no call present → no config emitted.
- `ModelMergerTests`: config applies override; no config leaves `IsRequiredOverride`
  null; CLR `IsNullable` is untouched by the merge either way.
- `OnModelCreatingRewriterTests`: all four insertion cases for both `true` and
  `false`, plus mutate-existing (both directions) and remove.

## Out of scope

- No UI/diagnostic surfacing of a CLR-vs-fluent disagreement (e.g. `string` CLR type
  but `.IsRequired(false)`) — that's a future consumer's concern once the Blazor shell
  exists.
- No validation that `IsRequiredOverride` and `IsNullable` are consistent; the model
  simply stores both facts.

## Addendum (post-review): multi-config chain-position fix

The whole-branch final review (after Tasks 1-6 landed) found that
`FluentSyntaxHelpers.GetPropertyNameFor` only resolved a config call's
property when that call was the *immediate* receiver of `Property(...)`.
Once a property carries two chained config calls — e.g.
`entity.Property(e => e.Name).IsRequired().HasMaxLength(100)` — appending a
second config bumps the first one out of that position, so re-editing or
removing the earlier-added config silently failed (duplicating a call, with
EF's last-wins semantics discarding the user's newest edit, or no-op'ing a
remove). This was unreachable with `HasMaxLength` alone but became reachable
the moment `IsRequired` shipped alongside it.

Fixed (Task 7) by making `GetPropertyNameFor` walk the full receiver chain —
trying `GetPropertyNameForPropertyCall` at each level until it finds the
`Property(...)` call — instead of only checking the immediate receiver. This
fixes the bug for both `HasMaxLength` and `IsRequired`, and for any future
config kind that reuses the same helper, since they all route through it.
