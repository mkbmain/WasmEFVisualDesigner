# SQL-shaped mapping: HasComputedColumnSql, HasCheckConstraint, HasSequence/UseSequence — design

> Backlog item: "SQL-shaped mapping the DBA will look for first" (`docs/backlog.md`,
> Priority 2), remaining scope after `HasDefaultValueSql`/`HasDefaultSchema`/constraint
> naming shipped. Also fixes the follow-up `RecognizedCallNames` scoping bug noted in
> the same backlog item.

## Architecture

Each new fluent call follows the existing five-layer pipeline (Model → Parser → Merger
→ Rewriter → Editor → UI), reusing generic helpers where the call shape matches an
existing feature and adding new machinery only where the shape is genuinely new:

- **HasComputedColumnSql** — property-level, one string arg + optional bool. Reuses
  the generic string-arg-call helper family in `OnModelCreatingRewriter` that
  `HasDefaultValueSql`/`HasColumnType` already share, extended with an optional second
  argument.
- **HasCheckConstraint** — entity-level, repeatable (zero or more per entity), 2-arg
  (name, sql). No existing helper fits a repeatable per-entity call; gets its own model
  list and its own add/set/remove rewriter methods.
- **HasSequence** (model-level declaration, chained with `StartsAt`/`IncrementsBy`/
  `HasMin`/`HasMax`/`IsCyclic`) and **UseSequence** (property-level reference to a
  declared sequence) — both new. Model-level parsing follows the `ParseDefaultSchema`/
  `FindModelLevelCalls` pattern, extended to walk the chained tail for the five
  sub-configs.
- **RecognizedCallNames scoping fix** — a correctness fix to the existing
  `ParseUnrecognizedCalls` recognition mechanism, needed so the new call names (and
  the previously-shipped `HasName`) don't silently swallow diagnostics for lookalike
  chains they don't actually apply to.

All new model fields are nullable/empty-default trailing record parameters, so no
existing constructor call site (including test fixtures) needs to change.

## Feature 1 — HasComputedColumnSql

**Model**: `PropertyModel.ComputedColumnSql` (`string?`) and
`PropertyModel.ComputedColumnSqlIsStored` (`bool?`, `null` = unspecified/omitted),
both new trailing parameters.

**Parser**: `FluentConfigParser.ParseComputedColumnSqls(sourceCode)` mirrors
`ParseDefaultValueSqls`: walks each entity's config scope, finds `HasComputedColumnSql`
calls, resolves the property via `GetPropertyNameFor`, reads arg 0 as a string literal
(else emits `UnreadableHasComputedColumnSqlArgument`), and reads an optional second
`stored:` bool argument (named or positional) into `IsStored` — a non-literal second
arg is left `null` rather than erroring, since it's optional in real EF usage. Produces
`ComputedColumnSqlConfig(EntityName, PropertyName, Sql, IsStored)`.

**Merger**: `ModelMerger.ApplyComputedColumnSqls` uses the existing `IndexByProperty`
join helper, same shape as `ApplyDefaultValueSqls`.

**Rewriter**: the generic string-arg-call helper family (`BuildStringArgCall`,
`MutateExistingStringArgCall`, `AppendStringArgCallToPropertyCall`,
`InsertStringArgPropertyStatement`, `InsertStringArgEntityBlock`,
`RemoveStringArgCall`) gains an optional `bool? secondArg = null` parameter that
appends `, stored: true/false` to the built call when non-null. Every existing caller
(`HasColumnType`, `HasDefaultValueSql`) passes no value and is unaffected.
`SetComputedColumnSql(sourceCode, entityName, propertyName, sql, isStored)` and
`RemoveComputedColumnSql` are thin wrappers, structured identically to
`SetDefaultValueSql`/`RemoveDefaultValueSql`.

**Editor**: `DiagramEditor.SetComputedColumnSql(entityName, propertyName, sql, isStored)`
— normalize (blank → clear), no-op if unchanged, resolve the declaring entity via
`ResolveDeclaringEntity` (TPH-safe), delegate to the rewriter. Same shape as
`SetDefaultValueSql`.

**UI**: `EntityNode.razor` gets a "Computed column SQL" text input plus a "Stored"
checkbox in the property detail panel, next to the existing default-value-SQL field,
committed through the file's `SafeEdit` wrapper.

**Diagnostic**: `UnreadableHasComputedColumnSqlArgument`. `"HasComputedColumnSql"`
added to `RecognizedCallNames` (plain entry — no known name collision).

## Feature 2 — HasCheckConstraint

**Model**: new `CheckConstraintModel(string Name, string Sql)` record in
`Core/Model`. `EntityModel.CheckConstraints` (`IReadOnlyList<CheckConstraintModel>`,
defaults to an empty list, same pattern as `Indexes`/`AlternateKeys`).

**Parser**: `FluentConfigParser.ParseCheckConstraints(sourceCode)` — for each entity's
config scope, uses `FindCallsNamed(scope, "HasCheckConstraint")` (called directly on
the entity/builder receiver as its own top-level statement, not chained onto another
call — no `WalkChainedTail` needed). Reads args 0 and 1 as string literals into
`Name`/`Sql`; either failing emits `UnreadableHasCheckConstraintArgument`. Produces one
`CheckConstraintConfig(EntityName, Name, Sql)` per call; an entity may have any number.

**Merger**: `ModelMerger.ApplyCheckConstraints` groups configs by entity name and
replaces `entity.CheckConstraints` with the full list for that entity (whole-list
replace, not per-field merge, since each constraint is one unit).

**Rewriter**: three new `OnModelCreatingRewriter` methods (no existing helper fits a
repeatable per-entity call):
- `AddCheckConstraint(sourceCode, entityName, name, sql)` — inserts a new
  `entity.HasCheckConstraint("name", "sql");` statement into the entity's scope,
  creating the scope if absent (same fallback `SetKey`/`SetDefaultValueSql` use).
- `SetCheckConstraint(sourceCode, entityName, oldName, newName, newSql)` — finds the
  existing statement whose first string-literal argument equals `oldName` and replaces
  both arguments.
- `RemoveCheckConstraint(sourceCode, entityName, name)` — finds that statement and
  removes it, applying the same "remove the parent `GlobalStatementSyntax` instead of
  the inner statement" handling from the F1 bug fix rather than reintroducing that
  crash class.

**Editor**: `DiagramEditor.AddCheckConstraint` / `SetCheckConstraint` /
`RemoveCheckConstraint(entityName, ...)` — validate the new/changed name is non-empty
and unique among the entity's existing check-constraint names (case-sensitive,
matching EF), then delegate to the rewriter.

**UI**: `EntityNode.razor` gets a new "Check constraints" list section on the entity
card: one row per constraint (name input, SQL input, remove button) plus an "Add check
constraint" button. Every commit wrapped in `SafeEdit`.

**Diagnostic**: `UnreadableHasCheckConstraintArgument`. `"HasCheckConstraint"` added to
`RecognizedCallNames` (plain entry).

## Feature 3 — HasSequence / UseSequence

**Model**: new `SequenceModel(string Name, string? Schema, string? ClrType,
long? StartsAt, int? IncrementsBy, long? MinValue, long? MaxValue, bool? IsCyclic)`
record in `Core/Model`. `DiagramModelResult` gets a new
`IReadOnlyList<SequenceModel> Sequences` member alongside `Entities`/`Relationships`/
`Diagnostics` (sequences are model-wide, not owned by any entity).
`PropertyModel.SequenceName` / `PropertyModel.SequenceSchema` (both `string?`, new
trailing parameters) record a property's `UseSequence` link.

**Parser**:
- `FluentConfigParser.ParseSequences(sourceCode)` — uses `FindModelLevelCalls` (as
  `ParseDefaultSchema` does) to find each `modelBuilder.HasSequence<T>("Name",
  schema: ...)` call, reads the name/schema arguments and the generic type argument
  into `ClrType`, then walks the chained tail off that same invocation (the same
  technique `ParseRelationshipChain`/`ReadIndexExtras` use for `HasForeignKey`/
  `HasIndex`) collecting `StartsAt`/`IncrementsBy`/`HasMin`/`HasMax`/`IsCyclic`.
  Unreadable name/schema arguments emit `UnreadableHasSequenceArgument`; an unreadable
  chained numeric/bool argument is skipped (left `null`) rather than erroring, since
  each chained option is independently optional in real EF usage.
- `FluentConfigParser.ParseUseSequences(sourceCode)` — entity-scoped,
  `FindCallsNamed(scope, "UseSequence")` chained onto a `.Property(...)` call (property
  resolved via `GetPropertyNameFor`, as `HasComputedColumnSql` does), reading the
  name/schema arguments. Unreadable arguments emit `UnreadableUseSequenceArgument`.

**Merger**: `ModelMerger.ApplySequences` sets the top-level `Sequences` list directly
(no per-entity join, since sequences aren't entity-scoped).
`ModelMerger.ApplyUseSequences` joins `SequenceName`/`SequenceSchema` onto matching
properties via the existing `IndexByProperty` helper.

**Rewriter**: `SetSequence` / `RemoveSequence` (model-level) rebuild the whole
`modelBuilder.HasSequence<T>(...).StartsAt(...)....` chain fresh on every edit via the
existing `ChainCall` helper, omitting any chained call whose value is `null` — the same
"rebuild the whole statement, omit absent options" pattern `BuildHasKeyStatement` and
`BuildHasIndexStatement` already use, so no separate "remove one chained option" logic
is needed. `SetUseSequence` / `RemoveUseSequence` reuse the generic string-arg
property-call helpers, extended to take two string arguments (sequence name + optional
schema) the same way Feature 1 extends them with a bool.

**Editor**: `DiagramEditor.AddSequence` / `SetSequence` / `RemoveSequence(name, ...)`
(model-level — a new kind of editor method, not entity-scoped) and
`DiagramEditor.SetUseSequence(entityName, propertyName, sequenceName, sequenceSchema)`.

**UI**: a new top-level "Sequences" panel (next to the existing diagnostics panels)
listing all declared sequences with inline edit fields and add/remove controls, plus a
"Uses sequence" input in `EntityNode.razor`'s property detail panel (a dropdown sourced
from the declared sequence names, falling back to free text).

**Diagnostics**: `UnreadableHasSequenceArgument`, `UnreadableUseSequenceArgument`.
`"HasSequence"` added to `RecognizedModelLevelCallNames`; `"UseSequence"` added to
`RecognizedCallNames` (entity-scope, plain entry).

## Feature 4 — RecognizedCallNames scoping fix

**Problem**: `FluentConfigParser.RecognizedCallNames` is one flat `HashSet<string>`
checked purely by method name in `ParseUnrecognizedCalls` (`FluentConfigParser.cs:47`).
Recognizing `"HasName"` globally — needed for `HasKey().HasName(...)` and
`HasIndex().HasName(...)` — also silently swallows `HasAlternateKey(...).HasName(...)`
and `HasSequence(...).HasName(...)`, neither of which any `Parse*` method reads. Those
should still be flagged `UnrecognizedConfigCall`, as they were before `HasName` was
added to the set.

**Fix**:
1. Add `FluentSyntaxHelpers.GetOwnerCallName(InvocationExpressionSyntax call)`: returns
   the method name of the call `call` is chained onto — i.e. given
   `call.Expression is MemberAccessExpressionSyntax { Expression: InvocationExpressionSyntax inner }`,
   returns `inner`'s method name, or `null` if `call` is chained directly onto the
   entity/builder receiver rather than another call. This is a one-hop lookup using
   information already available at each call site visited by `WalkChainDown`/
   `WalkChainedTail` — no new traversal.
2. In `FluentConfigParser`, add
   `private static readonly Dictionary<string, HashSet<string>> ContextSensitiveCallNames`
   mapping a chained-call name to the set of owner-call names it's actually read under:
   `{ "HasName", { "HasKey", "HasIndex" } }`. `HasDatabaseName`/`HasConstraintName`
   aren't ambiguous with anything else today, so they stay in the plain
   `RecognizedCallNames` set unchanged — only `HasName` (the one documented
   false-positive) moves to the context-sensitive table.
3. `ParseUnrecognizedCalls` checks `ContextSensitiveCallNames` first: if `methodName` is
   a key, the call is recognized only when `GetOwnerCallName(call)` is in that key's
   allowed-owner set; otherwise it falls through to being flagged even though the bare
   name is `"HasName"`. If `methodName` is not a context-sensitive key, fall back to the
   existing plain `RecognizedCallNames.Contains(methodName)` check, unchanged.

This is scoped to exactly the documented bug — none of the ~40 other names in
`RecognizedCallNames` change behavior — and is extensible: a future ambiguous name just
adds one dictionary entry instead of requiring a structural change.

## Testing

- **Parser**: new `FluentConfigParserTests` cases per feature — happy path, unreadable
  argument (diagnostic + correct fallback value), and absence (no call → no config, no
  diagnostic) for `ParseComputedColumnSqls`, `ParseCheckConstraints`, `ParseSequences`
  (one case per chained sub-option: `StartsAt`/`IncrementsBy`/`HasMin`/`HasMax`/
  `IsCyclic`, plus one exercising all five together), and `ParseUseSequences`. Plus the
  two scoping-fix regression cases: `HasAlternateKey(...).HasName(...)` and
  `HasSequence(...).HasName(...)` still fire `UnrecognizedConfigCall`, while existing
  `HasKey().HasName()`/`HasIndex().HasName()`/`.HasDatabaseName()` recognition tests
  continue passing unchanged.
- **Merger**: `ModelMergerTests` cases per `Apply*` method — sets the field/list on the
  matching entity/property, leaves others untouched.
- **Rewriter**: `OnModelCreatingRewriterTests` cases per Set/Remove/Add method —
  insert-when-absent, mutate-when-present, remove-clears-the-call, and (for
  `HasCheckConstraint`) add/remove-by-name with multiple constraints on one entity.
- **Editor**: `DiagramEditorPropertyPanelTests` (or a new
  `DiagramEditorSequenceTests`/`DiagramEditorCheckConstraintTests` file for the
  model-level and list-shaped methods) — happy path, no-op-when-unchanged, validation
  failure (duplicate check-constraint name, unknown entity/property).
- **UI markup coverage**: no new test needed — `GestureHandlerSafeEditTests`'
  `EveryEditorMutationCall_IsWrappedInSafeEdit` generically covers any new
  `EditContext.Editor.*` call added to `EntityNode.razor`/the new sequences panel, as
  long as each is wrapped in `SafeEdit`.
- **Round-trip fuzz**: extend `RoundTripFuzzTests` fixtures to include
  `HasComputedColumnSql`, `HasCheckConstraint` (multiple, to catch add/remove-by-name
  bugs), and `HasSequence`/`UseSequence`, asserting parse → edit → reparse survives
  each.
