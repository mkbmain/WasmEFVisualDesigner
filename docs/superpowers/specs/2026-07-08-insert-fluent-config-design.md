# Insert New Fluent Config Where None Exists — Design

**Date:** 2026-07-08
**Status:** Approved, ready for implementation plan

## Problem

`docs/backlog.md` Priority 1 first bullet:

> `OnModelCreatingRewriter` can only replace an existing `HasMaxLength` arg.
> It cannot add a `HasMaxLength` (or any call) to a property that has none.
> Requires generating a new statement into a lambda body while preserving
> surrounding trivia/indentation.

Today `OnModelCreatingRewriter.RewriteMaxLength` finds an existing
`HasMaxLength(...)` call for the given entity/property and swaps its numeric
literal argument. If no such call exists, it throws
`InvalidOperationException`. This is a real gap: a user editing a property
that currently has no `HasMaxLength` configured (or whose entity has no
config at all) cannot set one through the tool.

## Scope

`RewriteMaxLength(source, entityName, propertyName, newMaxLength)` becomes a
single entry point that resolves to one of four cases, checked in order of
"how much is missing":

1. **`HasMaxLength` call already exists** for entity.property — mutate the
   numeric literal argument. Unchanged from today's behavior, including the
   byte-identical round-trip guarantee (no insertion happens, so no
   reformatting happens).
2. **`entity.Property(e => e.X)` call exists, no `.HasMaxLength(...)`
   chained onto it** — append a `.HasMaxLength(N)` invocation onto the
   existing member-access chain.
3. **`Entity<T>(...)` block exists for the entity, but no `Property()` call
   for this property** — synthesize a new
   `entity.Property(<param> => <param>.X).HasMaxLength(N);` statement and
   append it as the last statement in the block's body.
4. **No `Entity<T>(...)` block exists for the entity at all** — synthesize
   the whole `modelBuilder.Entity<T>(entity => { ... });` block (containing
   the one new `Property().HasMaxLength()` statement) and append it as the
   last statement in `OnModelCreating`'s body.

**Out of scope:** `OnModelCreating` itself missing from the `DbContext`
class (no override, or the class can't be found). This throws
`InvalidOperationException`, same as an unresolvable target throws today.
Scaffolding a missing method override is a distinct, larger problem left for
a future backlog item.

Callers do not need to know which case applies — `RewriteMaxLength` inspects
the tree and picks the minimal edit.

## Naming resolution for synthesized code

- **`modelBuilder` receiver identifier** (case 4): read from
  `OnModelCreating`'s actual parameter name in the method being edited, not
  hardcoded — consistent with the P0 fix that made *reading* fluent config
  tolerant of a renamed parameter (`FluentSyntaxHelpers.GetConfiguredEntityName`).
  If `OnModelCreating` can't be located, this is the out-of-scope throw case
  above.
- **`Entity<T>` lambda parameter** (case 4): always `"entity"`, matching the
  convention used throughout this codebase's fixtures and tests.
- **`Property()` lambda parameter** (cases 3 and 4): copied from an existing
  sibling `Property(...)` call in the same `Entity<T>` block if one exists
  (e.g. reuse `"x"` if siblings use `entity.Property(x => x.Name)`); falls
  back to `"e"` when the block has no existing `Property()` calls to infer
  from (including the brand-new block synthesized in case 4).

## Formatting strategy

Insertion cases (2, 3, 4) call `NormalizeWhitespace()` on the whole document
after inserting the new node, then return `newRoot.ToFullString()`.

This is a deliberate simplification over scoping normalization to just the
new subtree: it reformats the *entire* file to Roslyn's default style, not
only the entity/statement being touched. Untouched entities' blocks may
shift indentation or brace style as a side effect of an edit to a different
entity. This trades a larger diff for significantly simpler implementation
(no manual trivia-copying/splicing logic). Case 1 (pure literal mutation,
no insertion) is unaffected by this and remains byte-identical, since
`NormalizeWhitespace` is only invoked on the insertion paths.

## Test impact

- `RoundTripTests.Parse_Merge_NoEdit_RegeneratesConfigIdenticalToOriginal`
  and `Parse_Edit_Regenerate_ChangesOnlyTheEditedProperty` exercise case 1
  only (mutating `Person.Name`'s existing `HasMaxLength`) — unaffected,
  stay byte-identical assertions.
- New tests for cases 2–4 must assert on **parsed values** (via
  `FluentConfigParser.ParseMaxLengths` on the regenerated source) rather
  than exact text equality, since whole-file normalization changes
  formatting. Cover:
  - Case 2: `Property()` exists, no `HasMaxLength` → appended.
  - Case 3: `Entity<T>` block exists, property never mentioned → new
    statement appended, sibling lambda param name matched.
  - Case 3 variant: block is otherwise empty → falls back to `"e"`.
  - Case 4: entity has no `Entity<T>` block at all → whole block
    synthesized and appended to `OnModelCreating`, `modelBuilder` param
    name resolved correctly (including a renamed param, e.g. `builder`).
  - Out-of-scope case: `OnModelCreating` not found → still throws
    `InvalidOperationException` (existing exception type/behavior).
  - A test confirming an *unrelated* entity's config values are unaffected
    by an insertion elsewhere in the file (values survive round-trip
    through `FluentConfigParser` even though its formatting changed).

## Non-goals

- Adding/dropping/renaming whole properties or entities (separate P1
  backlog bullets).
- Any fluent call other than `HasMaxLength` (P2 backlog).
- Preserving exact original formatting on insertion (explicitly traded away
  above).
