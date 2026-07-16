# `IEntityTypeConfiguration<T>` write-back — design

> Backlog: Priority 1, "`IEntityTypeConfiguration<T>` is parse-only — edits
> can't be written back" (`docs/backlog.md`).

## Problem

`FluentConfigParser` reads config out of both `modelBuilder.Entity<T>(...)`
blocks and `IEntityTypeConfiguration<T>.Configure(EntityTypeBuilder<T>
builder)` classes — `FluentSyntaxHelpers.FindConfigurationScopes` already
yields `(EntityName, Scope)` pairs for both shapes. But `OnModelCreatingRewriter`,
which every diagram edit is routed through, only knows how to find and mutate
`Entity<T>(...)` invocations (via `FluentSyntaxHelpers.FindEntityConfigInvocations`).
A project whose config lives in `IEntityTypeConfiguration<T>` classes — a
style the README advertises as supported — renders correctly on load, but
every diagram edit either silently no-ops or synthesizes a *new*,
disconnected `Entity<T>(...)` block in `OnModelCreating` alongside the
class the user actually wrote, producing duplicate/conflicting config.

## Goal

Every `OnModelCreatingRewriter` mutator writes into whichever scope an
entity's config already lives in — `Entity<T>()` block or
`IEntityTypeConfiguration<T>` class — transparently, with no new input from
`DiagramEditor` or the caller (still a single `ConfigSource` string).

## Non-goals

- Detecting a project's "dominant style" to choose where brand-new entities
  (added via the diagram's "+ Entity" button) get minted. New entities
  always get a `modelBuilder.Entity<T>(...)` block in `OnModelCreating`,
  unchanged from today.
- Merging or reconciling an entity that has config in *both* shapes
  simultaneously — see "Both-styles tie-break" below; this is a
  deterministic precedence rule, not a merge.
- Any UI change. `DiagramEditor`'s public surface and the `ConfigSource`
  contract are unchanged.

## Architecture

### Scope generalization

Introduce a scope abstraction used by every mutator's lookup step, replacing
direct calls to `FindEntityConfigInvocations`:

1. Call `FluentSyntaxHelpers.FindConfigurationScopes(root)` to get all
   `(EntityName, Scope)` pairs for the target entity name.
2. Apply the tie-break rule (below) to pick one `Scope` node if more than
   one exists for the entity.
3. Extract the scope's statement list uniformly via a new helper,
   `GetScopeBody(SyntaxNode scope)`:
   - If `scope` is an `InvocationExpressionSyntax` (the `Entity<T>()` call),
     return the lambda argument's block-body statement list — this is the
     existing behavior, just factored out.
   - If `scope` is a `MethodDeclarationSyntax` (the `Configure` method),
     return its body block's statement list directly.
4. Every mutate-existing-call / append-onto-existing-`Property()`-call /
   insert-new-statement tier that currently walks `Entity<T>()`'s lambda
   body now walks whichever statement list `GetScopeBody` returned.
5. The "no scope exists at all for this entity" tier is unchanged: mint a
   new `modelBuilder.Entity<T>(...)` block in `OnModelCreating`, exactly as
   today (see Non-goals).

This touches all ~23 public `OnModelCreatingRewriter` methods, but as a
single shared refactor of their common lookup step rather than 23 separate
changes — the four-tier fallback pattern (mutate → append → insert
statement → synthesize block) stays intact, only the "find existing calls"
step is generalized.

### Receiver-chain matching

Inside a `Configure` method, fluent calls read `builder.Property(b =>
b.Name).HasMaxLength(100)` — the receiver resolves to the method's own
parameter, not a nested lambda parameter as in the `Entity<T>()` case.
`FluentSyntaxHelpers`'s chain-walking (`GetPropertyNameFor` and friends)
already matches by shape rather than a hardcoded identifier, but currently
only verifies the receiver traces back to an `Entity<T>()` lambda's
parameter. Extend that check to also accept "traces back to the single
parameter of the enclosing method when the enclosing type implements
`IEntityTypeConfiguration<T>>`." Verified per-method by the test matrix
below rather than assumed to work automatically.

### Rename handling

`RenameEntityReferences` currently patches `Entity<T>`/`DbSet<T>`
type-argument occurrences. For an entity configured via
`IEntityTypeConfiguration<T>`, it must additionally patch:
- The base-list generic argument: `class BlogConfig :
  IEntityTypeConfiguration<Blog>` → `...<NewName>`.
- The `Configure` method's parameter type: `Configure(EntityTypeBuilder<Blog>
  builder)` → `Configure(EntityTypeBuilder<NewName> builder)`.

Without this, a rename leaves the config class referencing a deleted type
and the file no longer compiles.

### Remove handling

`RemoveEntity` gains a branch on scope kind: if the entity's scope is a
`Configure` method, delete the entire enclosing class declaration (not just
the method body) from the config source, in addition to the existing
DbSet<T>/POCO removal. This mirrors today's "remove the whole `Entity<T>()`
block" behavior at the class-declaration level.

### Both-styles tie-break

If an entity has config in both an `Entity<T>()` block and an
`IEntityTypeConfiguration<T>` class (not prevented by any existing
validation), mutators prefer the `Entity<T>()` scope. This matches current
behavior when only that style exists and keeps the rule deterministic
without attempting to merge or diagnose the overlap. Not addressed by this
design: emitting a diagnostic for this overlap — that's a separate,
smaller follow-up if it turns out to matter in practice.

## Testing

Extend `OnModelCreatingRewriterTests` (currently ~85+ tests against the
`Entity<T>()` shape) with:
- A parallel `IEntityTypeConfiguration<T>`-sourced fixture run through the
  same method matrix: all mutate/append/insert/synthesize tiers across the
  ~15 config kinds (`HasMaxLength`, `IsRequired`, `HasPrecision`, `HasKey`,
  `HasIndex`, table/column/default-value mappings, relationships).
- Rename patching the class base-list generic argument and `Configure`
  parameter type.
- Remove deleting the whole config class, not just the method.
- The both-styles tie-break (entity configured both ways → edits land in
  the `Entity<T>()` block, `IEntityTypeConfiguration<T>` class untouched).
- New-entity add still synthesizes into `OnModelCreating` even when the
  rest of the file uses `IEntityTypeConfiguration<T>` exclusively.

No changes needed to `EntityClassParser`, `FluentConfigParser`,
`ModelMerger`, `DiagramModelBuilder`, or `DiagramEditor` — this is entirely
contained within `OnModelCreatingRewriter` and the `FluentSyntaxHelpers`
methods it calls.
