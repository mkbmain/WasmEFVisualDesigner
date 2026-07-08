# FluentConfigParser Hardening — Design

**Date:** 2026-07-08
**Status:** Approved, ready for implementation plan

## Problem

`docs/backlog.md` Priority 0 lists four related trust gaps, all in
`FluentConfigParser.cs` / `FluentSyntaxHelpers.cs`, explicitly deferred by the
2026-07-08 diagnostics-channel/EntityClassParser plan:

1. **Hardcoded `modelBuilder` receiver name** — `GetConfiguredEntityName`
   requires the identifier be literally `modelBuilder`. A renamed lambda
   param (`builder`, `b`) makes the whole entity's config vanish silently.
2. **`Property` string-overload and block-bodied lambdas not read** —
   `GetPropertyNameFor` only handles `entity.Property(e => e.Name)`
   (expression-bodied simple lambda). `entity.Property("Name")` (which
   scaffold emits) and block-bodied lambdas are dropped.
3. **Non-literal `HasMaxLength` arguments dropped silently** —
   `int.TryParse` fails on `HasMaxLength(MaxNameLength)` or
   `HasMaxLength(50 * 2)` and the call is skipped with no signal.
4. **Diagnostics channel not wired into `FluentConfigParser`** — the
   `Diagnostic`/`ParseResult<T>` types exist (from the prior plan) and
   `EntityClassParser` populates them, but `FluentConfigParser` still returns
   a bare list and silently drops everything it can't read.

Combined, these mean a user configuring `HasMaxLength` through any
moderately different style than the single example fixture sees data loss
with zero warning — the core trust problem the diagnostics channel exists to
solve.

## Scope

In scope: the four items above, confined to `FluentSyntaxHelpers.cs` and
`FluentConfigParser.cs`.

Out of scope (explicitly deferred, not this pass):

- Following `OnModelCreating` calls that delegate to a helper method
  (`ConfigurePerson(modelBuilder)`) which itself calls `.Entity<T>(...)`.
  Fixing item 1 is a **shape match** (any identifier + generic `.Entity<T>`
  member access), not a call-graph traversal.
- Resolving non-literal `HasMaxLength` arguments to actual values (would
  require a `Compilation`/semantic model, not just syntax). These are
  diagnosed, not resolved.
- Any fluent config other than `HasMaxLength` (`IsRequired`, `HasPrecision`,
  keys, indexes, relationships — all separate backlog items).

## Design

### 1. Receiver-name shape match

`GetConfiguredEntityName` (`FluentSyntaxHelpers.cs:78`) changes its match
condition from requiring `Expression: IdentifierNameSyntax { Identifier.Text:
"modelBuilder" }` to requiring `Expression: IdentifierNameSyntax` (any
identifier). The rest of the shape check (member access named `Entity`,
generic, one type argument) is unchanged. This is a one-line condition
change; no new helper needed.

This also has a side benefit: it makes the nested-entity-lambda-param
handling (`entity =>`, `nested =>` in `FindCallsNamed`) an intentional
consequence of the shape match rather than something that happened to work
only because those params weren't checked against `"modelBuilder"`.

### 2. `Property` string-overload + block-bodied lambda

`GetPropertyNameFor` (`FluentSyntaxHelpers.cs:53`) gains two additional
resolution paths, tried in order after the existing expression-bodied-lambda
path fails:

- **String overload:** `entity.Property("Name")` — if the sole argument to
  `Property(...)` is a `LiteralExpressionSyntax` with a string token, return
  its value directly.
- **Block-bodied lambda:** `entity.Property(e => { return e.Name; })` — if
  the lambda argument is a `SimpleLambdaExpressionSyntax` with a `Block`
  containing exactly one `ReturnStatementSyntax` whose expression is a
  `MemberAccessExpressionSyntax`, extract the member name from that. Any
  other block shape (multiple statements, no return, non-member-access
  return) is not resolved — falls through to `null` and (per item 4) becomes
  an `UnresolvablePropertyName` diagnostic when reached from
  `ParseMaxLengths`.

### 3 & 4. Diagnostics for non-literal args and unresolved property names

`FluentConfigParser.ParseMaxLengths` changes signature:

```csharp
public ParseResult<IReadOnlyList<MaxLengthConfig>> ParseMaxLengths(string sourceCode)
```

Two new diagnostic-producing branches inside the existing loop over
`HasMaxLength` invocations:

- **`UnresolvablePropertyName`** — when `GetPropertyNameFor` returns `null`.
  `EntityName` is set, `PropertyName` is `null`, span is the `HasMaxLength`
  call's span.
- **`UnreadableMaxLengthArgument`** — when a property name *was* resolved but
  `int.TryParse` on the argument fails (non-literal expression: const
  identifier, arithmetic, etc.). `EntityName` and `PropertyName` both set,
  span is the argument's span.

Both diagnostics are collected into a `List<Diagnostic>` alongside the
existing `results` list and returned together in the `ParseResult`.

If `arg is null` (no argument at all to `HasMaxLength()`) that remains a
silent skip — this is a compile error in the source being parsed, not a
pattern-recognition gap, so it's out of scope for diagnostics here.

### Caller updates

- `tests/EfSchemaVisualizer.Core.Tests/RoundTripTests.cs` — 3 call sites of
  `new FluentConfigParser().ParseMaxLengths(...)` change to `....Value`.
- `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs` calls
  `GetPropertyNameFor` directly, not `ParseMaxLengths` — unaffected by the
  `ParseResult` wrapping, and transparently benefits from fix #2 (more
  property-selector shapes resolve correctly).

## Testing

New `[Fact]` tests added to `FluentConfigParserTests.cs`, following the
existing raw-string-literal fixture style:

- Renamed receiver (`builder =>` instead of `modelBuilder =>`) still resolves
  the entity and its `HasMaxLength` configs.
- `entity.Property("Name").HasMaxLength(100)` (string overload) resolves.
- `entity.Property(e => { return e.Name; }).HasMaxLength(100)` (block-bodied
  lambda, single return) resolves.
- `entity.Property(e => e.Name).HasMaxLength(MaxNameLength)` (const
  identifier argument) — config not added, `UnreadableMaxLengthArgument`
  diagnostic emitted with correct entity/property name.
- `entity.Property(e => e.Name).HasMaxLength(50 * 2)` (arithmetic expression
  argument) — same diagnostic path.
- A `HasMaxLength` call whose `Property(...)` lambda body is unresolvable
  (e.g. multi-statement block) — no config added, `UnresolvablePropertyName`
  diagnostic emitted.
- Existing two tests in `FluentConfigParserTests.cs` updated to unwrap
  `.Value` and assert `.Diagnostics` is empty for the happy path.

## Backlog update

After implementation, check off in `docs/backlog.md` Priority 0:

- "Hardcoded `modelBuilder` receiver name."
- "`Property` string-overload and block-bodied lambdas not read."
- "Non-literal `HasMaxLength` arguments dropped silently."
- "Parser silently drops config it doesn't understand — add a diagnostics /
  'unsupported' channel." (fully checked now — both `EntityClassParser` and
  `FluentConfigParser` populate it.)
