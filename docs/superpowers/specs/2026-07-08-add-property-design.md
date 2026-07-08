# Add Property to Entity Class — Design

**Date:** 2026-07-08
**Status:** Approved, ready for implementation plan

## Problem

`docs/backlog.md` Priority 1:

> **Add a property** to an entity (POCO class + optional config).

Today the codebase can parse existing properties (`EntityClassParser`) and
insert/mutate fluent config for a property that already exists on the class
(`OnModelCreatingRewriter`). There is no way to add a brand-new property to
the POCO class itself. This is the first of the add/drop/rename editing
capabilities called out in the backlog as the user's explicitly requested
"full ability to add properties, drop them, etc."

## Scope

New type: `EntityClassRewriter` in `src/EfSchemaVisualizer.Core/CodeGen/`,
alongside `OnModelCreatingRewriter`.

```csharp
public sealed class EntityClassRewriter
{
    public string AddProperty(string sourceCode, string className, PropertyModel property);
}
```

- Reuses the existing `PropertyModel(Name, ClrType, IsNullable, MaxLength)`
  record as the input shape. `MaxLength` is **ignored** by this method —
  fluent config insertion is a separate concern already handled by
  `OnModelCreatingRewriter.RewriteMaxLength`. Callers that want "add a
  property with a max length" compose both calls themselves; this method
  does not orchestrate that.
- Locates the target type declaration (`ClassDeclarationSyntax`,
  `RecordDeclarationSyntax`, or `StructDeclarationSyntax`) by `className`,
  restricted to **top-level** type declarations only — the same rule
  `EntityClassParser.Parse` already applies
  (`!t.Ancestors().OfType<TypeDeclarationSyntax>().Any()`), so a nested type
  with a matching name is never mistaken for the target.
- If no matching top-level type declaration is found, throws
  `InvalidOperationException`, consistent with `OnModelCreatingRewriter`'s
  existing convention for "target not found" (e.g. `OnModelCreating`
  missing).

## Property synthesis

Always synthesizes a full auto-property, regardless of what accessor style
other properties in the class use:

```
public {ClrType}{?} {Name} { get; set; }
```

- `?` appended to the type when `property.IsNullable` is true.
- Appended as the **last member** of the type's member list — after all
  existing members (fields, properties, methods), not positioned near other
  properties or inferring insertion point from sibling structure. This
  matches `OnModelCreatingRewriter.InsertPropertyStatement`'s "append as
  last statement" convention.
- No attempt to infer `init` vs `set`, attributes, or other accessor styles
  from sibling properties — always plain `get; set;`.

## Formatting strategy

Same trade-off already established in `OnModelCreatingRewriter`'s insertion
paths: call `NormalizeWhitespace()` on the whole tree after inserting the
new member, then `ToFullString()`. This reformats the entire file to
Roslyn's default style rather than preserving original trivia — a
deliberate simplification, trading a larger diff for no trivia-splicing
logic. There is no byte-identical/no-op case for this method (every call
inserts something), so there's no equivalent to `RewriteMaxLength`'s "case
1" that stays untouched.

## Non-goals

- Wiring the new property into fluent config automatically (separate
  composition via `OnModelCreatingRewriter`, not this feature).
- Record positional parameters — only body properties are synthesized.
  Adding a positional parameter to a record's parameter list is a distinct,
  more complex edit (affects the primary constructor signature) left for a
  future backlog item if needed.
- Dropping or renaming properties (separate P1 backlog bullets).
- Inferring accessor style (`init` vs `set`) or attributes from sibling
  properties.
- Property name collision handling (adding a property that already exists)
  — not addressed; behavior in that case is whatever falls out of blindly
  appending (a duplicate member, which the C# compiler would reject, but
  this tool does not validate).

## Testing

New `tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs`:

- Append a non-nullable property to a class with existing properties —
  assert new property present, existing properties/comments untouched.
- Append to a class with zero properties (empty body).
- Nullable `PropertyModel.IsNullable == true` produces a `?`-suffixed type.
- Record with existing body properties — property appended to the record's
  member list (not the positional parameter list).
- Target class name not found in source → throws `InvalidOperationException`.
- Multiple top-level type declarations in one file — only the named one is
  modified, others untouched (mirrors `OnModelCreatingRewriter`'s
  "untouched sibling entity" test pattern).
