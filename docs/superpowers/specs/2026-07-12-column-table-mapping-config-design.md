# Column / Table Mapping Fluent Config — Design

**Status:** approved, ready for planning
**Backlog item:** Priority 2 — `[spec] Column/table mapping — ToTable, HasColumnName, HasColumnType, default values`

## Problem

Nothing today models an entity's table mapping or a property's column mapping. `ToTable(...)`, `HasColumnName(...)`, `HasColumnType(...)`, and `HasDefaultValue(...)` calls are invisible to the parser, merger, and rewriter. This item adds full round-trip support for all four, each following the same parse → merge → rewrite pattern already established for `HasMaxLength`/`HasKey`, with one config type per fluent call kind (consistent with how `MaxLengthConfig`, `IsRequiredConfig`, `KeyConfig`, and `IndexConfig` are each their own type rather than one combined config).

## Model changes

`EntityModel` gains two new entity-level fields (table mapping is entity-level, like `KeyPropertyNames`):

```csharp
public sealed record EntityModel(
    string Name,
    IReadOnlyList<PropertyModel> Properties,
    IReadOnlyList<string> KeyPropertyNames,
    IReadOnlyList<IndexModel> Indexes,
    string? TableName = null,
    string? Schema = null);
```

- `TableName` is `null` when no `ToTable(...)` call configures the entity.
- `Schema` is `null` when `ToTable` was called with one argument, or when there's no call at all.

`PropertyModel` gains three new nullable string fields (column mapping is per-property, like `MaxLength`):

```csharp
public sealed record PropertyModel(
    string Name,
    string ClrType,
    bool IsNullable,
    int? MaxLength,
    bool? IsRequiredOverride = null,
    int? Precision = null,
    int? Scale = null,
    string? ColumnName = null,
    string? ColumnType = null,
    string? DefaultValueLiteral = null);
```

- `ColumnName`/`ColumnType` are `null` when the corresponding call is absent.
- `DefaultValueLiteral` stores the *exact source text* of the literal argument to `HasDefaultValue(...)` (e.g. `"5"`, `"\"active\""`, `"true"`, `"null"`), not a parsed/typed value. This lets a single string field represent any CLR literal type without a discriminated union, and guarantees byte-identical round-trip of the literal on rewrite. `null` when absent.

## Parsing: four new `FluentConfigParser` methods

All four follow the existing `ParseMaxLengths`/`ParseKeys` shape: walk each `Entity<T>` config invocation, find the relevant call, read literal arguments, emit a diagnostic and leave the field unset on anything non-literal.

### `ParseTableMappings`

```csharp
public ParseResult<IReadOnlyList<TableConfig>> ParseTableMappings(string sourceCode)
public sealed record TableConfig(string EntityName, string TableName, string? Schema);
```

Finds `ToTable(...)` called directly on the entity builder receiver (not chained off `Property(...)` — same shape as `HasKey`).

- **One string literal:** `ToTable("Orders")` → `TableName = "Orders", Schema = null`.
- **Two string literals:** `ToTable("Orders", "sales")` → `TableName = "Orders", Schema = "sales"`.
- **Anything else** (non-literal argument) → diagnostic `UnreadableToTableArgument`, entity's table mapping left unset.

### `ParseColumnNames`

```csharp
public ParseResult<IReadOnlyList<ColumnNameConfig>> ParseColumnNames(string sourceCode)
public sealed record ColumnNameConfig(string EntityName, string PropertyName, string ColumnName);
```

Finds `HasColumnName(...)` chained off `Property(...)` (via `GetPropertyNameFor`).

- **One string literal argument** → `ColumnName` set.
- **Non-literal argument** → diagnostic `UnreadableHasColumnNameArgument`, left unset.

### `ParseColumnTypes`

```csharp
public ParseResult<IReadOnlyList<ColumnTypeConfig>> ParseColumnTypes(string sourceCode)
public sealed record ColumnTypeConfig(string EntityName, string PropertyName, string ColumnType);
```

Finds `HasColumnType(...)` chained off `Property(...)`.

- **One string literal argument** (e.g. `"decimal(18,2)"`) → `ColumnType` set verbatim.
- **Non-literal argument** → diagnostic `UnreadableHasColumnTypeArgument`, left unset.

### `ParseDefaultValues`

```csharp
public ParseResult<IReadOnlyList<DefaultValueConfig>> ParseDefaultValues(string sourceCode)
public sealed record DefaultValueConfig(string EntityName, string PropertyName, string LiteralText);
```

Finds `HasDefaultValue(...)` chained off `Property(...)`.

- **One literal argument** (string, numeric, bool, char, or `null` literal — any `LiteralExpressionSyntax`) → `LiteralText` set to that literal's exact source text (`arg.ToString()`).
- **Anything else** (member access like `DateTime.UtcNow`, method call, enum member, negative-number unary expression, etc.) → diagnostic `UnreadableHasDefaultValueArgument`, left unset. (Negative numeric literals parse as a `PrefixUnaryExpressionSyntax` wrapping a literal, not a bare `LiteralExpressionSyntax` — out of scope for this pass, same class of gap as the parenthesized-lambda item already tracked in the backlog.)

If multiple calls of the same kind exist for the same entity/property (not valid EF but syntactically possible), `ModelMerger` resolves ambiguity by taking the first, same `FirstOrDefault` convention as existing configs.

## Merging: four new `ModelMerger` methods

```csharp
public static EntityModel ApplyTableMapping(EntityModel entity, IReadOnlyList<TableConfig> configs)
public static EntityModel ApplyColumnNames(EntityModel entity, IReadOnlyList<ColumnNameConfig> configs)
public static EntityModel ApplyColumnTypes(EntityModel entity, IReadOnlyList<ColumnTypeConfig> configs)
public static EntityModel ApplyDefaultValues(EntityModel entity, IReadOnlyList<DefaultValueConfig> configs)
```

Each is a simple lookup (`ApplyTableMapping` by `EntityName`; the other three by `(EntityName, PropertyName)`) and set; no match leaves the field(s) unset. Four separate composed calls, consistent with how existing `Apply*` methods compose rather than orchestrate.

## Rewriting: `OnModelCreatingRewriter`

### Table mapping — entity-level, mirrors `SetKey`/`RemoveKey`

```csharp
public string SetTable(string sourceCode, string entityName, string tableName, string? schema)
public string RemoveTable(string sourceCode, string entityName)
```

- Emits `ToTable("Name")` when `schema` is `null`, `ToTable("Name", "schema")` otherwise.
- Same three-case dispatch as `SetKey`: mutate existing `ToTable(...)` call, insert a new statement into an existing `Entity<T>` block, or synthesize a whole new block. No bare-receiver append case (same reasoning as `HasKey` — not chained off `Property`).
- `RemoveTable` deletes the matching statement entirely (no bare-receiver fallback), no-op if absent.

### Column name / column type / default value — per-property, mirror `RewriteMaxLength`/`RemoveMaxLength`

```csharp
public string SetColumnName(string sourceCode, string entityName, string propertyName, string columnName)
public string RemoveColumnName(string sourceCode, string entityName, string propertyName)

public string SetColumnType(string sourceCode, string entityName, string propertyName, string columnType)
public string RemoveColumnType(string sourceCode, string entityName, string propertyName)

public string SetDefaultValue(string sourceCode, string entityName, string propertyName, string literalText)
public string RemoveDefaultValue(string sourceCode, string entityName, string propertyName)
```

Each `Set*` uses the same four-case dispatch as `RewriteMaxLength`/`RewritePrecision` (mutate, append-to-bare-`Property`, insert-statement, synthesize-block). `SetDefaultValue` emits `HasDefaultValue(literalText)`, splicing `literalText` directly into the call — it is already valid C# literal source text by construction (it only ever comes from a `DefaultValueConfig.LiteralText` produced by the parser, or a caller who follows the same contract).

Each `Remove*` strips its call, leaving the bare `Property()` call in place (same as `RemoveMaxLength`), no-op if absent.

That is 8 new public methods (4 `Set*`/`SetTable` + 4 `Remove*`/`RemoveTable`), all mechanical repeats of an already-proven pattern — no new codegen technique required.

## New diagnostic codes

`UnreadableToTableArgument`, `UnreadableHasColumnNameArgument`, `UnreadableHasColumnTypeArgument`, `UnreadableHasDefaultValueArgument`. `UnreadableToTableArgument` carries entity name only (`PropertyName: null`, same convention as `UnreadableHasKeyArgument`); the other three carry both entity and property name.

## Testing

New test coverage mirroring the existing `HasMaxLength`/`HasKey` suites, per config kind:

- `FluentConfigParserTests`: happy path (including two-arg `ToTable`), non-literal argument → diagnostic, no call present → field(s) null, for each of the four kinds.
- `ModelMergerTests`: config applies its field(s); no config leaves them null; other fields untouched, for each of the four kinds.
- `OnModelCreatingRewriterTests`: `Set*`/`SetTable` for all applicable dispatch cases (mutate, append where applicable, insert-statement, synthesize-block), plus `Remove*`/`RemoveTable` (existing call removed; no-op when absent), for each of the four kinds.

## Out of scope

- No validation that `ColumnType` strings are valid SQL, or that `TableName`/`ColumnName` don't collide.
- `HasDefaultValueSql(...)` (raw SQL default expression) — a distinct EF call from `HasDefaultValue`, deferred to a future item.
- Non-literal default values (member access, method calls, enum members, unary-negative numeric literals) — surfaced via diagnostic, not silently dropped, but not resolved in this pass.
- No support for `[Table]`/`[Column]` data-annotation attributes (CLR-side) — fluent API only, matching existing items.
- No UI consumer yet (Blazor shell is Priority 4) — this is model/parse/rewrite only.
