# Rename an Entity or Property — Design

**Date:** 2026-07-08
**Status:** Approved, ready for implementation plan

## Problem

`docs/backlog.md` Priority 1:

> **Rename** an entity or property (class member + every referencing fluent
> call + lambda body).

`EntityClassRewriter` and `OnModelCreatingRewriter` can add, mutate, and
remove properties and their `HasMaxLength` config, but there is no way to
rename an entity class or a property while keeping the POCO, the
`OnModelCreating` fluent config, and the `DbSet<T>` declaration in sync.
This is the third of the add/drop/rename editing capabilities in the
backlog's P1 section.

## Scope

Four separate methods, matching the split established by `AddProperty` /
`RemoveProperty` (POCO edits and fluent-config edits stay independent
rewriters; callers compose them). Rename is implemented as a direct
identifier/token swap, **not** as remove-then-add — remove+add would
reorder a renamed property to the end of the member list (losing its
original position and any attached comments) and would lose the
`HasMaxLength` argument value that a pure rename must carry forward
unchanged.

### 1. `EntityClassRewriter.RenameClass`

```csharp
public string RenameClass(string sourceCode, string oldClassName, string newClassName);
```

- Locates the target top-level type declaration using the same rule as
  `AddProperty`/`RemoveProperty`
  (`!t.Ancestors().OfType<TypeDeclarationSyntax>().Any()`).
- Throws `InvalidOperationException` if no top-level type named
  `oldClassName` is found — consistent with existing methods.
- Renames the type declaration's `Identifier` token to `newClassName`.
- Also renames the `Identifier` token of any `ConstructorDeclarationSyntax`
  member on that type whose name equals `oldClassName`, so an explicit
  constructor stays in sync and the file keeps compiling. (POCOs rarely
  declare one, but C# requires the names to match.)
- `NormalizeWhitespace().ToFullString()` on the edited tree.

### 2. `EntityClassRewriter.RenameProperty`

```csharp
public string RenameProperty(string sourceCode, string className, string oldPropertyName, string newPropertyName);
```

- Locates the target top-level type the same way as `RenameClass`. Throws
  `InvalidOperationException` if not found.
- Finds the `PropertyDeclarationSyntax` member on that type whose
  `Identifier.Text == oldPropertyName`. Throws `InvalidOperationException`
  if no such property exists — same failure mode as `RemoveProperty`.
- Renames that property's `Identifier` token to `newPropertyName`.
- `NormalizeWhitespace().ToFullString()` on the edited tree.
- Record positional parameters are not supported — only body properties are
  renameable, matching `AddProperty`/`RemoveProperty`'s existing scope.

### 3. `OnModelCreatingRewriter.RenameEntityReferences`

```csharp
public string RenameEntityReferences(string sourceCode, string oldEntityName, string newEntityName);
```

- Single pass over the DbContext file fixing both places an entity's type
  name appears as a bare type argument:
  - Every `Entity<OldName>(...)` invocation (found via
    `FluentSyntaxHelpers.GetConfiguredEntityName`) — rename the type
    argument to `newEntityName`.
  - Every `DbSet<OldName>` property declaration on any type in the file
    (`PropertyDeclarationSyntax` whose `Type` is a `GenericNameSyntax` named
    `DbSet` with a single type argument `IdentifierNameSyntax.Identifier.Text
    == oldEntityName`) — rename the type argument to `newEntityName`.
- If neither pattern matches anywhere in the file: **no-op**, return the
  source unchanged. Absence is a normal state (e.g. renaming an entity that
  has no fluent config yet, or whose `DbSet<T>` lives in a different file
  this call isn't given), not an error — same reasoning as
  `RemoveMaxLength`'s no-op path.
- If any match is found: `NormalizeWhitespace().ToFullString()` on the
  edited tree.

### 4. `OnModelCreatingRewriter.RenamePropertyReferences`

```csharp
public string RenamePropertyReferences(string sourceCode, string entityName, string oldPropertyName, string newPropertyName);
```

- Reuses the existing lookup pattern: `FluentSyntaxHelpers
  .FindEntityConfigInvocations(root, entityName)` →
  `FindCallsNamed(entityInvocation, "Property")` → match via
  `GetPropertyNameForPropertyCall(call) == oldPropertyName` (`FirstOrDefault`,
  matching the "at most one `Property()` call per property" assumption
  `RewriteMaxLength`/`RemoveMaxLength` already make).
- Renames whichever form `GetPropertyNameForPropertyCall` resolved:
  - Expression-bodied lambda (`e => e.Name`) — rename the
    `MemberAccessExpressionSyntax.Name` identifier.
  - Block-bodied lambda (`e => { return e.Name; }`) — rename the same
    identifier inside the `return` statement's member access.
  - String overload (`"Name"`) — replace the string literal's text.
- If no matching `Property()` call is found: **no-op**, return the source
  unchanged (e.g. renaming a property that isn't fluently configured at
  all — a normal state, not an error).
- If found: `NormalizeWhitespace().ToFullString()` on the edited tree.

**No orchestration** combining these four — an end-to-end "rename
everywhere" operation is the caller's responsibility to compose (typically
all four, applied to the entity file and the DbContext file respectively),
same as `AddProperty`/`RemoveProperty` left composition to the caller.

## Formatting strategy

Same trade-off as every mutation path in this codebase:
`NormalizeWhitespace()` on the whole tree when an edit is made,
`ToFullString()` return; verbatim passthrough of the original source string
on no-op paths (methods 3 and 4 only — methods 1 and 2 always either edit or
throw, they have no no-op path).

## Non-goals

- Combined/orchestrated "rename everywhere in one call" entry point.
- Renaming free-text references to the old name elsewhere in a file
  (comments, unrelated locals, XML doc text).
- Parenthesized-lambda `Property((Person e) => e.Name)` calls — existing,
  separately tracked gap in `GetPropertyNameForPropertyCall`
  (`docs/backlog.md` Priority 2). `RenamePropertyReferences` simply won't
  find a match for these today, same as every other method built on that
  helper.
- Any fluent config kind other than `Property`/`HasMaxLength` (none other
  exists yet).
- Record positional parameters, for both class and property rename.
- Cross-file consistency checking (e.g. verifying the caller actually
  applied all four renames) — each method is independently correct on the
  one file it's given.

## Testing

New tests in `tests/EfSchemaVisualizer.Core.Tests/CodeGen/EntityClassRewriterTests.cs`:
- `RenameClass`: renames a plain class; renames a record; renames a
  struct; renames a class with an explicit constructor (constructor name
  updated too); class name not found → throws; multiple top-level types —
  only the target is renamed.
- `RenameProperty`: renames an existing body property, siblings untouched;
  renames on a record's body properties; property not found → throws;
  class not found → throws; multiple top-level types — only the target
  type's property is touched.

New tests in `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`:
- `RenameEntityReferences`: renames `Entity<OldName>` type argument;
  renames `DbSet<OldName>` property declaration; renames both when present
  in the same file; no `Entity<>` config and no `DbSet<T>` for that name →
  no-op, source unchanged; multi-entity source — renaming one entity
  doesn't affect a sibling entity's `Entity<>`/`DbSet<T>` references.
- `RenamePropertyReferences`: renames expression-bodied lambda form;
  renames block-bodied lambda form; renames string-overload form; no
  matching `Property()` call → no-op, source unchanged; multi-entity
  source — renaming one entity's property doesn't affect a sibling
  entity's same-named property.
