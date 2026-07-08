# Drop a Property — Design

**Date:** 2026-07-08
**Status:** Approved, ready for implementation plan

## Problem

`docs/backlog.md` Priority 1:

> **Drop a property** (remove from class and remove any of its config statements).

`EntityClassRewriter.AddProperty` (just shipped) can insert a new property
into a class/record/struct. There is no way to remove one, nor to clean up
any fluent config (`HasMaxLength`) that referenced it. This is the second
of the add/drop/rename editing capabilities in the backlog's P1 section.

## Scope

Two separate methods, matching the split established by `AddProperty` (POCO
edits and fluent-config edits stay independent rewriters; callers compose
them):

### 1. `EntityClassRewriter.RemoveProperty`

```csharp
public string RemoveProperty(string sourceCode, string className, string propertyName);
```

- Locates the target top-level type declaration using the same rule as
  `AddProperty`/`EntityClassParser.Parse`
  (`!t.Ancestors().OfType<TypeDeclarationSyntax>().Any()`).
- Throws `InvalidOperationException` if no top-level type named `className`
  is found — consistent with `AddProperty`.
- Finds the `PropertyDeclarationSyntax` member on that type whose
  `Identifier.Text == propertyName`.
- Throws `InvalidOperationException` if no such property member exists on
  the type — same failure mode as a missing class, no silent no-op.
- Removes that member from the type's member list (`RemoveNode` with
  default removal options), then `NormalizeWhitespace().ToFullString()`.

### 2. `OnModelCreatingRewriter.RemoveMaxLength`

```csharp
public string RemoveMaxLength(string sourceCode, string entityName, string propertyName);
```

- Reuses the existing lookup `RewriteMaxLength` already performs:
  `FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName)` →
  `FindCallsNamed(entityInvocation, "HasMaxLength")` → match via
  `GetPropertyNameFor(call) == propertyName`.
- If no matching `HasMaxLength` call is found: **no-op**, return the
  source unchanged. There is nothing to strip, and this is a distinct
  failure mode from "class not found" — an absent config statement is a
  normal, expected state (e.g. dropping a property that was never
  configured), not an error.
- If found: strip only the `.HasMaxLength(...)` invocation off the
  member-access chain, rewriting
  `entity.Property(e => e.Email).HasMaxLength(50)` down to
  `entity.Property(e => e.Email)`. The now-inert bare `Property()`
  statement is left in place — no cascading deletion of the statement
  itself. This is a deliberate simplification: the codebase does not track
  "is this Property() call meaningful," so removing it is out of scope
  (would require proving no other fluent call is chained on it, which
  isn't true today but could be once other config kinds land in P2).
- `NormalizeWhitespace().ToFullString()` on the edited tree.

**No orchestration** combining the two methods — a "drop a property"
end-to-end operation (POCO + config) is the caller's responsibility to
compose, same as `AddProperty` left "add a property with a max length" to
the caller.

## Formatting strategy

Same trade-off as every insertion/mutation path in this codebase:
`NormalizeWhitespace()` on the whole tree, `ToFullString()` return. No
trivia preservation.

## Non-goals

- Combined/orchestrated "drop everywhere in one call" entry point.
- Deleting the bare `Property()` statement left behind by
  `RemoveMaxLength` once its `HasMaxLength` is stripped.
- Any fluent config kind other than `HasMaxLength` (none other exists yet).
- Record positional parameters — only body properties are removable,
  matching `AddProperty`'s scope.
- Renaming (separate P1 backlog bullet).

## Testing

New tests in `tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs`:
- Remove an existing property from a class with siblings — property gone,
  siblings/comments untouched.
- Remove from a record's body properties.
- Property name not found on an existing class → throws
  `InvalidOperationException`.
- Class name not found → throws `InvalidOperationException`.
- Multiple top-level types — only the target type is modified.

New tests in `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`:
- `HasMaxLength` call exists for entity/property → stripped, bare
  `Property()` call remains, siblings untouched.
- No `HasMaxLength` call exists for that property → no-op, source
  unchanged (assert via `FluentConfigParser` showing no config, or
  string equality with the input).
- Entity exists but has no config invocation at all → no-op (same as
  above, entity-level absence).
- Multi-entity source — stripping one entity's config doesn't affect a
  sibling entity's `HasMaxLength` call.
