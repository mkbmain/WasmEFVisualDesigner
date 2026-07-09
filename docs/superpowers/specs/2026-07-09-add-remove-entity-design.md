# Add / Remove an Entity — Design

**Date:** 2026-07-09
**Status:** Approved, ready for implementation plan

## Problem

`docs/backlog.md` Priority 1:

> **Add / remove an entity** — mint a whole new `modelBuilder.Entity<T>(...)`
> block, or remove one, without disturbing siblings.

Today the codebase can add/drop/rename properties on an existing entity
class and fix up existing fluent-config references, but there is no way to
introduce or delete an entity itself — the POCO class, its `DbSet<T>`
registration on the `DbContext`, and its `modelBuilder.Entity<T>(...)`
configuration block. This is the last of the add/drop/rename editing
capabilities called out in the backlog's P1 section as the user's
explicitly requested "full ability to add properties, drop them, etc." —
extended one level up, from properties to whole entities.

Scope decision (confirmed with user): this covers the **full round-trip** —
POCO class, `DbSet<T>` property, and the `Entity<T>(...)` config block —
not just the narrower "OnModelCreating block only" reading of the backlog
text.

## Scope

Four new public methods across the two existing rewriter files, no new
files. Follows the same split every prior add/drop/rename feature used:
POCO-side and config-side are separate composed calls, not one orchestrated
operation. A caller wanting "add a brand-new entity end to end" calls
`EntityClassRewriter.AddClass` and `OnModelCreatingRewriter.AddEntity`
themselves; this design does not introduce an orchestrating method.

### 1. `EntityClassRewriter.AddClass`

```csharp
public string AddClass(string sourceCode, string className);
```

- Synthesizes an empty class:
  ```csharp
  public class {className}
  {
  }
  ```
  No properties are added — those come from separate `AddProperty` calls,
  matching `AddProperty`'s own non-goal of not orchestrating "add a
  property with a max length" in one call.
- Appended as the **last top-level member** of the compilation unit, after
  all existing top-level type declarations (or as the only member, if the
  file currently declares none). Matches `AddProperty`'s "append as last
  member, don't infer insertion point" convention.
- No collision detection — if a top-level type with the same name already
  exists, this blindly appends a second one (a duplicate declaration the
  C# compiler would reject, but this tool does not validate), matching
  `AddProperty`'s existing non-goal for property name collisions.
- `NormalizeWhitespace().ToFullString()` on the edited tree — no
  byte-identical case, every call inserts something (matches
  `AddProperty`).

### 2. `EntityClassRewriter.RemoveClass`

```csharp
public string RemoveClass(string sourceCode, string className);
```

- Locates the target top-level type declaration via the `FindTopLevelType`
  helper already extracted in this file (used by `AddProperty`,
  `RemoveProperty`, `RenameClass`, `RenameProperty`). Throws
  `InvalidOperationException` if no top-level type named `className` is
  found — consistent with every other `EntityClassRewriter` method's
  "target not found" convention (always throw, never no-op).
- Removes that type declaration from the compilation unit's member list
  (`RemoveNode`, `SyntaxRemoveOptions.KeepNoTrivia`), then
  `NormalizeWhitespace().ToFullString()`.

### 3. `OnModelCreatingRewriter.AddEntity`

```csharp
public string AddEntity(string sourceCode, string entityName, string dbSetPropertyName);
```

- `dbSetPropertyName` is a required, explicit parameter — no pluralization
  inference (`Person` → `People` is not a mechanical transform and this
  tool does not attempt it).
- Locates the `OnModelCreating` method the same way the existing private
  `InsertEntityBlock` does today; throws `InvalidOperationException` if
  none is found ("No OnModelCreating method found in source.") or if it
  has no body ("OnModelCreating has no method body."). This means
  `AddEntity` requires an existing `DbContext` file with a real
  `OnModelCreating` override — creating a `DbContext` class from scratch
  is out of scope.
- In one pass over the containing type declaration (the class that
  declares `OnModelCreating`):
  1. Appends an empty entity config statement to `OnModelCreating`'s body:
     ```csharp
     {modelBuilderParamName}.Entity<{entityName}>(entity =>
     {
     });
     ```
     Reuses the same statement shape the existing private `InsertEntityBlock`
     builds today, refactored to accept the inner `BlockSyntax` as a
     parameter so both the empty-block path (this method) and the
     property-carrying path (`RewriteMaxLength`'s existing insertion case)
     share one construction site instead of two near-identical copies.
  2. Appends a new property to the same containing type:
     ```csharp
     public DbSet<{entityName}> {dbSetPropertyName} { get; set; }
     ```
     Appended as the **last member** of the containing type (after
     `OnModelCreating` if that method happens to be last) — same "always
     append last, don't infer insertion point from convention" rule as
     `AddProperty`. A real-world `DbSet` block preceding `OnModelCreating`
     is a stylistic convention this tool does not try to preserve.
- No duplicate detection — if the entity already has a `DbSet<T>` property
  or `Entity<T>(...)` block, this blindly appends another (matches
  `AddClass`'s non-goal above).
- `NormalizeWhitespace().ToFullString()` on the edited tree.

### 4. `OnModelCreatingRewriter.RemoveEntity`

```csharp
public string RemoveEntity(string sourceCode, string entityName);
```

- Reuses `RenameEntityReferences`'s existing target-collection logic
  (unchanged): walks `Entity<T>(...)` invocations via
  `FluentSyntaxHelpers.GetConfiguredEntityName` and `DbSet<T>` property
  declarations, collecting every match for `entityName` — so multiple
  matches (e.g. an accidental duplicate) are all removed, not just the
  first.
- For each matched `Entity<T>(...)` invocation, removes the enclosing
  `ExpressionStatementSyntax` (the whole `modelBuilder.Entity<T>(...);`
  statement) — not just the invocation expression. Only the bare
  `modelBuilder.Entity<T>(entity => {...});` statement shape is handled;
  a chained form like `modelBuilder.Entity<T>(...).ToTable("X");` is out
  of scope (nothing else in this codebase handles chained calls after
  `Entity<T>()` either), and the statement is left untouched if the
  invocation is not the direct expression of its statement.
- For each matched `DbSet<T>` property declaration, removes that property
  member entirely.
- If **no** matches are found (neither an `Entity<T>()` block nor a
  `DbSet<T>` property for `entityName`): **no-op**, return `sourceCode`
  unchanged. An entity configured purely by EF Core convention — no
  explicit `Entity<T>()` call, or no `DbSet<T>` property because it's only
  reachable via navigation — is a normal, expected state, not an error.
  Matches `RemoveMaxLength`'s existing "absence is normal" rule.
- If any matches are found: remove all matched nodes in a single
  `RemoveNodes` pass, then `NormalizeWhitespace().ToFullString()`.

## Formatting strategy

Same trade-off as every insertion/mutation path in this codebase:
`NormalizeWhitespace()` on the whole tree, `ToFullString()` return. No
trivia preservation, consistent whole-file reformat.

## Non-goals

- A single orchestrating call that adds/removes the POCO class, `DbSet<T>`
  property, and config block together — callers compose `AddClass` +
  `AddEntity` (or `RemoveClass` + `RemoveEntity`) themselves, matching
  every prior add/drop/rename feature's split.
- Duplicate/collision detection on either `Add*` method.
- `record`/`struct` entity creation — `AddClass` always synthesizes
  `class`. (Renaming/other ops already handle existing records/structs;
  synthesizing a new one is not requested here.)
- Pluralization or any other naming inference for `dbSetPropertyName` —
  always an explicit caller-supplied argument.
- Removing a chained/fluent-extended `Entity<T>(...).X()` statement — only
  the bare, unchained statement shape is recognized.
- Creating a `DbContext` class or its `OnModelCreating` override from
  scratch — `AddEntity` requires both to already exist.
- Any validation that the new entity name doesn't collide with an existing
  one, or that `dbSetPropertyName` doesn't collide with an existing member.

## Testing

New tests in `tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs`:

- `AddClass`: appends a new empty class to a file with existing
  classes — new class present, existing classes/comments untouched.
- `AddClass`: appends to a file whose compilation unit has zero top-level
  type declarations (e.g. just `using`s) — new class becomes the only
  member.
- `RemoveClass`: removes an existing top-level class from a file with
  siblings — target gone, siblings/comments untouched.
- `RemoveClass`: class name not found → throws `InvalidOperationException`.
- `RemoveClass`: removing the only class in the file leaves a still-valid
  (near-empty) compilation unit.

New tests in `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`:

- `AddEntity`: on a source with an existing `DbSet<T>`/`OnModelCreating`
  for another entity — new `DbSet<NewEntity>` property and empty
  `modelBuilder.Entity<NewEntity>(entity => { });` block both present;
  existing entity's `DbSet`/config untouched.
- `AddEntity`: no `OnModelCreating` method in source → throws
  `InvalidOperationException`.
- `RemoveEntity`: source with `DbSet<T>` only (no config block) → property
  removed, no-op on the (absent) block.
- `RemoveEntity`: source with `Entity<T>(...)` block only (no `DbSet<T>`)
  → statement removed, no-op on the (absent) property.
- `RemoveEntity`: source with both → both removed in one call.
- `RemoveEntity`: neither present for the target entity → no-op, source
  returned unchanged (assert via string equality with the input, matching
  `RemoveMaxLength`'s existing no-op test pattern).
- `RemoveEntity`: multi-entity source — removing one entity's `DbSet`/block
  leaves a sibling entity's `DbSet`/block untouched (mirrors
  `RenameEntityReferences`'s existing sibling-isolation test pattern).
