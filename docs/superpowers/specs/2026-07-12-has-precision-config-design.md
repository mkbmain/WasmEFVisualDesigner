# `HasPrecision` Fluent Config — Design

**Status:** approved, ready for planning
**Backlog item:** Priority 2 — `[spec/plan] Precision / scale (HasPrecision) for decimal`

## Problem

Nothing today models a decimal property's precision/scale. `entity.Property(e => e.X).HasPrecision(p)` and `.HasPrecision(p, s)` calls are invisible to the parser, merger, and rewriter. This item adds full round-trip support, following the same parse → merge → rewrite pattern already established for `HasMaxLength`.

## Model changes

`PropertyModel` gains two new nullable fields:

```csharp
public sealed record PropertyModel(
    string Name,
    string ClrType,
    bool IsNullable,
    int? MaxLength,
    bool? IsRequiredOverride = null,
    int? Precision = null,
    int? Scale = null);
```

- `Precision` is `null` when no `HasPrecision(...)` call configures the property.
- `Scale` is `null` when `HasPrecision` was called with a single argument (precision only), or when there's no call at all.
- Mirrors the existing nullable-int pattern used by `MaxLength` — no new modeling concept.

## Parsing: `FluentConfigParser.ParsePrecisions`

New method, same shape as `ParseMaxLengths`:

```csharp
public ParseResult<IReadOnlyList<PrecisionConfig>> ParsePrecisions(string sourceCode)
```

New DTO:

```csharp
public sealed record PrecisionConfig(string EntityName, string PropertyName, int Precision, int? Scale);
```

Walk each `Entity<T>` config invocation, find calls named `HasPrecision` chained off a `Property(...)` call (via the existing `GetPropertyNameFor` receiver-chain walk). For each call found, read its argument list:

- **One literal int argument:** `HasPrecision(18)` → `Precision = 18, Scale = null`.
- **Two literal int arguments:** `HasPrecision(18, 2)` → `Precision = 18, Scale = 2`.
- **Anything else** (non-literal argument, e.g. `HasPrecision(MaxPrecision)`, `HasPrecision(18 * 1)`, method call, etc.) → new diagnostic code `UnreadableHasPrecisionArgument`, property's precision/scale left unset for this call — consistent with how non-literal `HasMaxLength` arguments are already handled.

If a property has multiple `HasPrecision(...)` calls (not valid EF but syntactically possible), `ModelMerger` resolves ambiguity by taking the first — same `FirstOrDefault` convention already used for other property-level configs.

## Merging: `ModelMerger.ApplyPrecisions`

```csharp
public static EntityModel ApplyPrecisions(EntityModel entity, IReadOnlyList<PrecisionConfig> configs)
```

Looks up a config by `(EntityName, PropertyName)` and sets `Precision`/`Scale` on the matching `PropertyModel`; no match leaves both `null`. Separate composed call, consistent with how `ApplyMaxLengths`/`ApplyIsRequired` are composed rather than orchestrated.

## Rewriting: `OnModelCreatingRewriter`

Two new public methods, reusing the exact four-case dispatch already built for `RewriteMaxLength` (mutate, append-to-bare-`Property`, insert-statement, synthesize-whole-block), with new `HasPrecision`-specific leaf builders:

```csharp
public string RewritePrecision(string sourceCode, string entityName, string propertyName, int precision, int? scale)
```

- Emits `HasPrecision(precision)` when `scale` is `null`, `HasPrecision(precision, scale)` otherwise.
- **Mutate:** replace an existing `HasPrecision(...)` call's argument list.
- **Append:** chain `.HasPrecision(...)` onto a bare `Property(...)` call that has no `HasPrecision` yet.
- **Insert statement:** add a new `entity.Property(e => e.X).HasPrecision(...)` statement into an existing `Entity<T>` block when the property has no config statement at all yet.
- **Synthesize block:** mint a whole new `Entity<T>(entity => { ... })` block when the entity isn't configured at all.

```csharp
public string RemovePrecision(string sourceCode, string entityName, string propertyName)
```

Strips a matching `.HasPrecision(...)` call, leaving the bare `Property()` call in place (same as `RemoveMaxLength`). No-op if no such call exists.

## New diagnostic code

`UnreadableHasPrecisionArgument` — entity name and property name populated, span pointing at the unreadable argument/call.

## Testing

New test coverage mirroring the existing `HasMaxLength` suite:

- `FluentConfigParserTests`: single-arg literal, two-arg literal, non-literal single arg → diagnostic, non-literal second arg → diagnostic, no `HasPrecision` call present → `Precision`/`Scale` both null.
- `ModelMergerTests`: config applies `Precision`/`Scale`; no config leaves both null; other fields untouched by the merge.
- `OnModelCreatingRewriterTests`: `RewritePrecision` for all four dispatch cases (mutate, append, insert-statement, synthesize-block) for both precision-only and precision+scale forms, plus `RemovePrecision` (existing call removed; no-op when absent).

## Out of scope

- No validation that `Precision`/`Scale` are sane for the property's CLR type (e.g. applying to a non-decimal property).
- No support for `[Precision]` data-annotation attributes (CLR-side) — fluent API only, matching `IsRequiredOverride`/`KeyPropertyNames`.
- No UI consumer yet (Blazor shell is Priority 4) — this is model/parse/rewrite only.
