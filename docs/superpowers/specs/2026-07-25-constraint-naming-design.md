# PK/FK constraint naming + index chain-form fix — design

## Background

`docs/backlog.md` Priority 2 lists a bundled bullet, "SQL-shaped mapping the DBA
will look for first," covering `HasDefaultValueSql`, `HasComputedColumnSql`,
`HasCheckConstraint`, `HasConstraintName`, `HasName`/`HasDatabaseName`,
`HasSequence`/`UseSequence`, `HasDefaultSchema`. Investigation found two of
these already fully implemented (`HasDefaultValueSql`, `HasDefaultSchema`) and
index naming via the string-arg overload (`HasIndex(x => x.Foo, "IX_Foo")`)
already parsed, mergeable, editable in the UI, and rewritable. The bullet is
being split into themes; this spec covers the **naming** theme:

1. Primary key constraint name: `entity.HasKey(x => x.Id).HasName("PK_Foo")`.
2. Foreign key constraint name: `.HasForeignKey(...).HasConstraintName("FK_Foo")`.
3. A read-fidelity fix for indexes: `HasIndex(...).HasDatabaseName("IX_Foo")`
   (or the legacy `.HasName(...)` alias) is not read today — only the
   string-arg overload is. Index naming is otherwise already fully
   round-trip editable; this closes the one remaining read gap so a file
   authored with the chained form doesn't silently lose its index name on
   parse.

Explicitly out of scope (separate backlog themes): `HasComputedColumnSql`,
`HasCheckConstraint`, `HasSequence`/`UseSequence`.

All new/changed fields are **full round-trip editable** (parse, merge,
diagram display, edit UI, rewrite), matching the existing pattern used for
fields like `ColumnType` and `DefaultValueSql` rather than the display-only
pattern used for e.g. `HasComment`.

## Data model

- `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`: add `string? KeyName = null`.
- `src/EfSchemaVisualizer.Core/Model/RelationshipModel.cs`: add `string? ConstraintName = null`.
- `src/EfSchemaVisualizer.Core/Merging/KeyConfig.cs`: add a `Name` field:
  `KeyConfig(string EntityName, IReadOnlyList<string> PropertyNames, string? Name)`.
- `src/EfSchemaVisualizer.Core/Merging/RelationshipConfig.cs`: add a
  `ConstraintName` field.

## Parsing (`FluentConfigParser` / `FluentSyntaxHelpers`)

- `ParseKeys`: for each `HasKey` call found in an entity's configuration
  scope, use `FluentSyntaxHelpers.WalkChainedTail` (same helper already used
  for `HasIndex` extras and the relationship `WithOne`/`WithMany` tail) to
  find a chained `HasName(...)` call. Read its first argument as a string
  literal; on success populate `KeyConfig.Name`, on failure emit a new
  `UnreadableHasKeyNameArgument` diagnostic (mirrors the existing
  `UnreadableHasKeyArgument` naming convention) without dropping the
  already-read property names.
- `ParseRelationshipChain`: the existing `WalkChainedTail(withCall, ...)`
  switch (which already recognizes `HasForeignKey`, `OnDelete`,
  `UsingEntity`) gets a new `HasConstraintName` case capturing the
  invocation. After the walk, read its first argument as a string literal
  into `RelationshipConfig.ConstraintName`; malformed arguments emit a new
  `UnreadableHasConstraintNameArgument` diagnostic.
- `ReadIndexExtras` (private helper inside `ParseIndexes`'s chain walk):
  extend the existing `switch (methodName)` with `HasDatabaseName` and
  `HasName` cases, reading a string literal into the index name. Precedence:
  if `TryReadIndexPropertyNames` already produced a name from the string-arg
  overload, that value wins (it's the same call, can't conflict in
  practice); otherwise the chained name is used. Malformed arguments reuse
  a diagnostic in the same family as the other index-extras cases
  (`UnreadableHasIndexArgument` — no new code needed since this augments an
  existing read path rather than introducing a new call).
- `RecognizedCallNames`: add `"HasName"`, `"HasConstraintName"`,
  `"HasDatabaseName"` so these chained calls stop firing
  `UnrecognizedConfigCall` now that they're actually read.

## Merging (`ModelMerger`)

- `ApplyKeys`: propagate `KeyConfig.Name` into `EntityModel.KeyName`.
- `ApplyRelationships`: propagate `RelationshipConfig.ConstraintName` into
  `RelationshipModel.ConstraintName`.

## CodeGen (`OnModelCreatingRewriter`)

- `SetKey(string sourceCode, string entityName, IReadOnlyList<string> propertyNames, string? name = null)`:
  new optional parameter, defaulting to `null` so all existing call sites
  compile unchanged. `BuildHasKeyStatement`/`MutateExistingKey` chain-append
  `HasName(name)` via the existing `ChainCall` helper when `name` is
  non-null (same technique `BuildHasIndexStatement` already uses for
  `HasFilter`/`IsUnique`). A null name and a previously-set name means the
  chained call is simply omitted on rewrite (the existing
  remove-and-rebuild-statement approach used by `MutateExistingKey` already
  achieves "remove if not present").
- `BuildRelationshipStatement`: add `AppendHasConstraintName(chain, relationship.ConstraintName)`,
  a new small helper alongside the existing `AppendOnDelete`, called after
  it in both the `OneToOne` and `OneToMany` branches (order doesn't matter
  functionally — EF resolves all of `HasForeignKey`/`OnDelete`/`HasConstraintName`
  against the same builder regardless of chain order — and this matches how
  the statement is always fully rebuilt on edit rather than patched in place).

## DiagramEditor / UI

- `DiagramEditor.SetKeyName(string entityName, string? name)`: looks up the
  entity, no-ops if unchanged, otherwise calls `_configRewriter.SetKey` with
  the entity's existing `KeyPropertyNames` and the new name — mirrors
  `SetIndexUnique`/`RenameIndex`'s "look up current value, delegate to the
  rewriter with everything else held constant" shape.
- `DiagramEditor.SetRelationshipConstraintName(RelationshipModel relationship, string? name)`:
  mirrors `SetRelationshipShape`'s remove-then-`SetRelationship` pattern
  (fails on inferred relationships the same way, for the same reason —
  there's no explicit statement yet to rewrite).
- `EntityNode.razor`: a small text input near the primary-key indicator
  (same visual pattern as the existing index-name input in the index
  detail panel), bound to `Node.Entity.KeyName`, wired to a new
  `CommitKeyName` handler calling `EditContext.Editor.SetKeyName(...)`.
- `RelationshipLinkLabel.razor`: a "Constraint name" text input added to the
  expanded panel for non-many-to-many relationships (alongside the existing
  "On delete" `<select>`), wired to a new `CommitConstraintName` handler
  calling `EditContext.Editor.SetRelationshipConstraintName(...)`.

## Diagnostics

New diagnostic codes in `DiagnosticCodes.cs`:
- `UnreadableHasKeyNameArgument`
- `UnreadableHasConstraintNameArgument`

No new codes for the index chain-form fix — it reuses the existing
`UnreadableHasIndexArgument` family since it augments an already-diagnosed
call rather than introducing a new one.

## Testing

- `FluentConfigParserTests`: `HasKey(...).HasName(...)` (valid string,
  missing arg, non-literal arg); `HasForeignKey(...).HasConstraintName(...)`
  (same three shapes); `HasIndex(...).HasDatabaseName(...)` and the legacy
  `.HasName(...)` alias, both with and without a name already set via the
  string-arg overload (confirming arg-overload precedence).
- `OnModelCreatingRewriterTests`: `SetKey` with a name — insert, update,
  clear (name → null removes the chained call); relationship rewrite
  includes `HasConstraintName` only when set.
- `DiagramEditorTests`: `SetKeyName` and `SetRelationshipConstraintName`
  end-to-end (parse → edit → reparse round trip), including the
  inferred-relationship failure case for the latter.
- `RoundTripFuzzTests`: extend the fuzz corpus/assertions so both new
  fields survive a parse → edit → rewrite → reparse cycle unchanged when
  untouched.

## Non-goals

- No UI/parsing support for computed columns, check constraints, or
  sequences — tracked separately in `docs/backlog.md`.
- No attempt to normalize or validate constraint name uniqueness/legality;
  the tool passes through whatever string literal is present, same as
  every other string-literal-backed field in this codebase.
