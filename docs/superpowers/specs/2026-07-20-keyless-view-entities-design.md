# Keyless / view entities — design

> Addresses the Round 3 Priority 3 backlog item in `docs/backlog.md`:
> "Keyless/view entities unread." Covers `HasNoKey()`, `ToView(...)`,
> `ToSqlQuery(...)`, and `[Keyless]`.

## Goal

EF Core supports entities with no primary key (`HasNoKey()` / `[Keyless]`),
typically used for database views (`ToView(...)`) or raw-SQL projections
(`ToSqlQuery(...)`). None of these four constructs are read today, so a
scaffolded database-first project with views renders those entities as
ordinary tables missing a key, with no signal to the user about why.

Unlike the previous round (Ignore/`[Index]`/value-generation/shadow
properties), this pass includes full write-back support: parsing, merging,
read-only-turned-editable diagram UI, and rewriter methods so the diagram can
toggle keyless-ness and set/clear view and SQL-query mappings.

## Model

`EntityModel` (`src/EfSchemaVisualizer.Core/Model/EntityModel.cs`) gains
three fields, all entity-level (no `PropertyModel` changes):

- `bool IsKeyless = false`
- `string? ViewName = null`
- `string? SqlQuery = null`

`Schema` (already present, used by `ToTable`) is reused as-is for `ToView` —
EF's own model shares the same schema concept between table and view
mapping, so no separate `ViewSchema` field is introduced.

## Parsing

New DTOs in `src/EfSchemaVisualizer.Core/Merging/`:

- `ViewConfig(string EntityName, string ViewName, string? Schema)`
- `SqlQueryConfig(string EntityName, string Sql)`

New `FluentConfigParser` methods, each following the existing
`ParseTableMappings`/`ParseIgnoredEntities` shapes:

- `ParseViewMappings(string sourceCode) -> ParseResult<IReadOnlyList<ViewConfig>>`.
  Within each `FluentSyntaxHelpers.FindConfigurationScopes` scope, finds
  calls named `ToView` via `FindCallsNamed`. First argument must be a string
  literal (the view name); a second string-literal argument, if present, is
  the schema. A non-literal first argument emits
  `DiagnosticCodes.UnreadableToViewArgument` and is skipped (mirrors
  `UnreadableToTableArgument`).
- `ParseSqlQueries(string sourceCode) -> ParseResult<IReadOnlyList<SqlQueryConfig>>`.
  Same shape, calls named `ToSqlQuery`, single string-literal argument (the
  raw SQL text). Non-literal argument emits
  `DiagnosticCodes.UnreadableToSqlQueryArgument`.
- `ParseKeylessEntities(string sourceCode) -> IReadOnlyList<string>`.
  Within each config scope, finds a bare `HasNoKey()` call (no arguments to
  misparse, so no `ParseResult`/diagnostic wrapper needed — same reasoning as
  `ParseIgnoredEntities`) and returns the owning entity names.

`EntityClassParser` gets `[Keyless]` attribute detection, following the
existing bare-presence-check pattern already used for `[NotMapped]`:
`FindAttribute(attributeLists, "Keyless") is not null` sets `IsKeyless: true`
directly in the `EntityModel` constructor call alongside the other
attribute-derived fields.

`"ToView"`, `"ToSqlQuery"`, and `"HasNoKey"` are added to
`FluentConfigParser.RecognizedCallNames` so they don't trip the
`UnrecognizedConfigCall` diagnostic.

Two new diagnostic codes in `DiagnosticCodes.cs`: `UnreadableToViewArgument`,
`UnreadableToSqlQueryArgument`.

## Merging

`ModelMerger` gains two new per-entity methods, copying `ApplyTableMapping`'s
`FirstOrDefault` → `entity with { ... }` shape:

- `ApplyViewMapping(EntityModel entity, IReadOnlyList<ViewConfig> configs)`
  → sets `ViewName`/`Schema`.
- `ApplySqlQuery(EntityModel entity, IReadOnlyList<SqlQueryConfig> configs)`
  → sets `SqlQuery`.

Keyless is folded in `DiagramModelBuilder.Build` (not `ModelMerger`, matching
how shadow properties needed builder-level knowledge) as a straight OR:
`entity.IsKeyless || fluentKeylessNames.Contains(entity.Name)`. Attribute and
fluent are additive, not conflicting — there's no scalar value to lose on a
bare boolean, so no fluent-wins tiebreak is needed here (unlike every other
dual-source config in this codebase).

## Rewriter (`OnModelCreatingRewriter`)

- `SetView(sourceCode, entityName, viewName, schema)` /
  `RemoveView(sourceCode, entityName)` — copy `SetTable`/`RemoveTable`'s
  four-case dispatch (mutate existing `ToView` call / append to existing
  scope block / insert new statement / synthesize a whole new
  `Entity<T>(...)` block) verbatim, just targeting `ToView` instead of
  `ToTable`.
- `SetSqlQuery(sourceCode, entityName, sql)` /
  `RemoveSqlQuery(sourceCode, entityName)` — same dispatch, single-argument
  call (`ToSqlQuery`), no schema.
- `SetKeyless(sourceCode, entityName)` / `RemoveKeyless(sourceCode, entityName)`
  — first entity-level bare-marker-call insert/remove in the codebase.
  `RemoveKeyless` follows `RemoveTable`'s find-call-and-delete-statement
  shape (locate `HasNoKey()`, confirm its parent is a top-level
  `ExpressionStatementSyntax`, `RemoveNode`; no-op if absent). `SetKeyless`
  inserts a bare `entity.HasNoKey();` statement via the same
  `GetScopeBlockAndReceiver` dispatch used everywhere else — there's no
  "mutate in place" case since the call takes no arguments, so it's just
  "already present → no-op" or "insert".

**Mutual exclusivity enforced at the rewriter boundary:** `SetKeyless` also
removes any existing `HasKey(...)` call for that entity (locate and delete
the statement, same mechanics as `RemoveKey`), and `SetKey` is extended to
remove any existing `HasNoKey()` call before writing the new key. This is a
hard EF invariant (a keyless entity cannot have a key — EF throws at
model-build time otherwise, and downstream FK/index logic in this codebase
assumes an entity's key is authoritative), so it's enforced unconditionally
rather than left to the user to avoid.

**Table vs. View are *not* cross-cleared.** Setting `ToView` does not remove
an existing `ToTable` call, and vice versa — EF itself throws at runtime if
both are configured, but auto-deleting a field the user didn't touch would
be a bigger behavior change than this app makes anywhere else today (e.g.
nothing today stops a user from setting `MaxLength` on a non-string
property). The rewriter emits whichever calls are set; if both end up
present, that's surfaced by EF at the user's own model-build time, not
prevented here. This is a deliberate scope line, not an oversight.

## DiagramEditor (`src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`)

Three new methods, each following `SetTableMapping`'s shape (look up entity
by name, normalize blank strings to null, no-op check against the entity's
current scalar fields, call the matching rewriter `Set*`/`Remove*`, then
`Apply`):

- `SetViewMapping(string entityName, string? viewName, string? schema)`
- `SetSqlQuery(string entityName, string? sql)`
- `SetKeyless(string entityName, bool isKeyless)` — no-op check against
  `entity.IsKeyless`; `true` → `_configRewriter.SetKeyless`, `false` →
  `_configRewriter.RemoveKeyless`.

## UI (`Diagram/EntityNode.razor`)

In the entity header, alongside the existing Table/Schema inputs:

- A new "View" text input (shares the existing Schema field), wired to
  `SetViewMapping`.
- A new "SQL query" text input, wired to `SetSqlQuery`.
- A new "Keyless (no primary key)" checkbox, wired to `SetKeyless`. When
  checked, the per-property primary-key toggle affordance (used for
  `HasKey`/`ToggleKey`) is hidden or disabled for that entity's property
  rows, since a keyless entity cannot carry a PK — mirrors how shadow
  property rows already suppress affordances that don't apply to them.

## Testing

Same shape as the precedent Ignore/Index/ValueGen/ShadowProps pass, plus
rewriter and editor coverage since this pass includes write-back:

- `FluentConfigParserTests`: `ParseViewMappings` (name only, name+schema,
  non-literal argument), `ParseSqlQueries` (literal, non-literal), and
  `ParseKeylessEntities`.
- `EntityClassParserTests`: `[Keyless]` attribute detection.
- `ModelMergerTests`: `ApplyViewMapping`, `ApplySqlQuery`.
- `DiagramModelBuilder`-level test: keyless via attribute only, via fluent
  only, via both (still just `true`, no duplicate/conflict).
- `OnModelCreatingRewriterTests`: all four dispatch cases (mutate / append /
  insert-statement / synthesize-block) for `SetView` and `SetSqlQuery`, plus
  their `Remove*` counterparts; `SetKeyless`/`RemoveKeyless` insert/remove
  cases; the two mutual-exclusion cases (`SetKeyless` strips an existing
  `HasKey`, `SetKey` strips an existing `HasNoKey`).
- `DiagramEditorTests`: `SetViewMapping`/`SetSqlQuery`/`SetKeyless`,
  including their no-op paths.
- A markup-source test (matching `EntityNodeAccessibilityTests`'s style) for
  the new View/SQL-query fields and Keyless checkbox rendering, and for the
  per-property PK-toggle suppression when `IsKeyless` is true.

## Explicitly out of scope

- Auto-clearing `ToTable` when `ToView` is set, or vice versa — see the
  rewriter section above.
- Any handling of `[Keyless]` combined with a `[Key]` attribute on a
  property in the same entity — undefined/contradictory input, not
  validated, matching this codebase's existing precedent of not validating
  other contradictory combinations (e.g. `HasPrecision` on a non-decimal
  property).
- Cascading effects of keyless-ness onto relationships/indexes beyond
  hiding the PK-toggle affordance (e.g. this pass does not attempt to warn
  when a keyless entity is used as a relationship's principal side, which
  EF itself disallows).
