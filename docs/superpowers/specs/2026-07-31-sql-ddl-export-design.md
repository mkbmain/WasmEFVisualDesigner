# SQL DDL Export

> Backlog item: Priority 3 — "SQL DDL export" (`docs/backlog.md`).

## Problem

A DBA using this tool thinks in tables and columns, not C#. Today the only
export formats are Mermaid `erDiagram` text and an SVG snapshot of the
diagram — neither is something you can run against a database, and neither
is a format a DBA would use to sanity-check "does this match what I meant?"
against their own mental model. `CREATE TABLE` is.

## Goal

A new "Export SQL" button next to the existing Export SVG / Export Mermaid
buttons. Clicking it downloads a `.sql` file containing dialect-specific DDL
generated purely from the parsed `DiagramModelResult` — no dependency on the
live `BlazorDiagram`, same posture as `MermaidExporter`. Supports all three
providers the scaffold feature already introduced: SQL Server, PostgreSQL,
SQLite.

## UI

A dialect dropdown (SQL Server / PostgreSQL / SQLite) and an "Export SQL"
button appear next to the existing Export SVG / Export Mermaid buttons,
enabled whenever `_editor is not null`. Defaults to whatever
`_selectedProvider` is currently set to if the scaffold checkbox has been
used this session (same enum, reused as-is — no separate dropdown state),
otherwise SQL Server.

```csharp
private async Task ExportSqlAsync()
{
    if (_editor is null) return;

    var sql = SqlDdlExporter.Export(_editor.Current, _selectedProvider);
    await DownloadTextAsync("schema.sql", sql, "text/plain");
}
```

## `SqlDdlExporter` (new, `EfSchemaVisualizer.Web.Diagram`)

```csharp
public static class SqlDdlExporter
{
    public static string Export(DiagramModelResult result, ScaffoldProvider provider);
}
```

Pure string generation, mirroring `MermaidExporter`'s shape. Statement order:

1. `CREATE SEQUENCE` for every `result.Sequences` entry (sequences can be
   referenced by a column's `IDENTITY`/default further down).
2. `CREATE TABLE` per physical table, in FK-safe topological order (every
   principal table before its dependents — reuses the same
   principal-before-dependent ordering `AutoLayout` already computes from
   `Relationships`, so no forward-reference errors in dialects that care).
   Each `CREATE TABLE` includes its columns, `PRIMARY KEY`, `CHECK`
   constraints, and alternate-key `UNIQUE` constraints inline.
3. `CREATE [UNIQUE] INDEX` statements for every `IndexModel`.
4. `ALTER TABLE ... ADD CONSTRAINT ... FOREIGN KEY` statements for every
   relationship with a real FK (`OneToOne`/`OneToMany`/`ManyToMany` join
   sides), emitted after all tables exist so ordering never matters for FKs
   specifically — only plain table-to-table dependencies are topologically
   sorted.

## Which entities get a table

- **Skipped entirely** (no `CREATE TABLE`, no columns, no FKs referencing
  them): `EntityModel.ViewName is not null` (view-mapped) and
  `EntityModel.FunctionName is not null` (function-mapped) — neither is a
  physical table.
- **`IsOwned` + folded into an owner** (the `OwnsOne` case): already absent
  from `result.Entities` — the model folds these away at parse time, nothing
  extra needed here.
- **`IsOwned` + standalone** (the `OwnsMany` case, entity still present):
  gets a plain `CREATE TABLE` from its own declared properties. No owner FK
  or ordinal column is synthesized, since `RelationshipKind.Owned` carries no
  `ForeignKeyProperties` today — documented non-goal below.
- **`IsSharedType`** (many-to-many join entities): gets a normal
  `CREATE TABLE`. Its `PRIMARY KEY` is the concatenation of
  `RelationshipModel.JoinEntityLeftForeignKey` and `JoinEntityRightForeignKey`
  (found via `Relationships.First(r => r.JoinEntityName == entity.Name)`),
  and it gets two FK constraints, one to each side of the relationship.
- Everything else gets one `CREATE TABLE` per `EntityModel`, subject to the
  inheritance-strategy branching below.

## Inheritance mapping strategy

Branches on the hierarchy root's `EntityModel.MappingStrategy`
(`Tph`/`Tpt`/`Tpc`), which parsing already resolved per hierarchy with
root-priority (see the mapping-strategy design). A "hierarchy" here means any
set of entities connected by `RelationshipKind.Inheritance` edges.

- **TPH** (default): one table, the root's `TableName`. Columns are the
  root's folded property list — which, per existing fold behavior, already
  includes every ancestor/descendant property merged in — plus the
  discriminator column (`DiscriminatorPropertyName`, defaulting to
  `"Discriminator"` if the property name isn't set but a value mapping
  exists). Derived entities do **not** get their own table.
- **TPT**: one table per entity in the hierarchy. The root's table has the
  full column set for its own properties; each derived entity's table has
  only the columns it declares itself (not inherited ones — mirrors how
  `InheritanceInference.Fold` already only folds the key for TPT) plus its
  own copy of the key column(s), which double as both `PRIMARY KEY` and a
  `FOREIGN KEY` back to the root's table (one-to-one PK-as-FK, the standard
  TPT shape).
- **TPC**: one table per **concrete** entity (leaves only — an abstract root
  with no direct rows still gets no table, but this codebase has no
  "abstract" flag today, so in practice every entity in a TPC hierarchy gets
  a table). Each table has the full folded column set independently, no
  shared PK/FK between them, matching how `Fold` already folds every
  ancestor property for TPC same as TPH.

## Column type mapping — `SqlColumnTypeMapper` (new, same file or a private
nested static class — internal helper, not part of the public surface)

One switch per provider, keyed on `PropertyModel.ClrType` first, with
`ColumnType` (explicit `HasColumnType`) always winning outright when present
(it's already a raw SQL type string). Baseline mapping (all three dialects
need a table since names differ):

| CLR type | SQL Server | PostgreSQL | SQLite |
|---|---|---|---|
| `int` | `int` | `integer` | `INTEGER` |
| `long` | `bigint` | `bigint` | `INTEGER` |
| `short` | `smallint` | `smallint` | `INTEGER` |
| `byte` | `tinyint` | `smallint` | `INTEGER` |
| `bool` | `bit` | `boolean` | `INTEGER` |
| `decimal` | `decimal(p,s)` | `numeric(p,s)` | `NUMERIC` |
| `double` | `float` | `double precision` | `REAL` |
| `float` | `real` | `real` | `REAL` |
| `string` | `nvarchar(n)`/`nvarchar(max)` | `varchar(n)`/`text` | `TEXT` |
| `Guid` | `uniqueidentifier` | `uuid` | `TEXT` |
| `DateTime` | `datetime2` | `timestamp` | `TEXT` |
| `DateTimeOffset` | `datetimeoffset` | `timestamptz` | `TEXT` |
| `DateOnly` | `date` | `date` | `TEXT` |
| `TimeOnly`/`TimeSpan` | `time` | `time` | `TEXT` |
| `byte[]` | `varbinary(max)` | `bytea` | `BLOB` |
| enum types (`IsEnumType`) | underlying CLR type mapping (int by default) | same | same |

- `MaxLength` fills `n` for `string`/`byte[]`; absent → `nvarchar(max)` /
  `varchar` unbounded / `text` per dialect. `IsUnicode == false` on SQL
  Server switches `nvarchar`→`varchar`; ignored on the other two dialects
  (no meaningful equivalent distinction in the same way).
  `IsFixedLength == true` switches `nvarchar`/`varchar`→`nchar`/`char`.
- `Precision`/`Scale` fill `decimal(p,s)`/`numeric(p,s)` when present;
  `decimal` with neither present falls back to each dialect's own default
  (`decimal(18,2)` SQL Server convention, `numeric` unscaled elsewhere).
- `ComputedColumnSql` replaces the whole column definition with the
  dialect's computed-column syntax: SQL Server
  `[Name] AS ({sql}) PERSISTED` when `ComputedColumnSqlIsStored != false`,
  `[Name] AS ({sql})` otherwise; PostgreSQL
  `"name" {type} GENERATED ALWAYS AS ({sql}) STORED` (Postgres only supports
  stored); SQLite `"name" {type} GENERATED ALWAYS AS ({sql}) STORED` (same
  restriction, matches SQLite's actual generated-column support).
- `DefaultValueLiteral` → `DEFAULT {literal}`; `DefaultValueSql` →
  `DEFAULT ({sql})`. Both are already stored as ready-to-emit C#/SQL literal
  text from prior parsing work, used verbatim.
- A property whose `ValueGenerated` indicates identity/auto-increment and
  that is also the (sole) primary key column gets `IDENTITY(1,1)` (SQL
  Server) / `GENERATED ALWAYS AS IDENTITY` (PostgreSQL) /
  `INTEGER PRIMARY KEY AUTOINCREMENT` inline, folded into the column instead
  of a separate `PRIMARY KEY` clause for the SQLite case specifically, since
  SQLite only supports autoincrement on an inline `INTEGER PRIMARY KEY`.
- `SequenceName` set on a property → `DEFAULT NEXT VALUE FOR [schema].[seq]`
  (SQL Server) / `DEFAULT nextval('schema.seq')` (PostgreSQL); SQLite has no
  sequence concept, so this becomes a plain column with no default and a
  comment noting the sequence was skipped.

## Identifier quoting per dialect

- SQL Server: `[Name]`, schema-qualified as `[Schema].[Table]` when
  `EntityModel.Schema` is set (default schema, if any, already resolved onto
  every entity by existing model-level `HasDefaultSchema` handling — nothing
  new needed here).
- PostgreSQL: `"name"`, same schema-qualification pattern with `"schema"."table"`.
- SQLite: bare `"name"` quoting for safety (reserved words), no schema
  support — `EntityModel.Schema` is ignored for SQLite with no diagnostic
  (there's nowhere to surface a UI warning from a pure exporter; documented
  as a known limitation instead, same posture as other exporter constraints).

## Testing

- `SqlColumnTypeMapperTests` (or inline cases within `SqlDdlExporterTests`):
  one case per CLR type × dialect combination in the table above, plus
  `MaxLength`/`Precision`/`Scale`/`IsUnicode`/`IsFixedLength` variants.
- `SqlDdlExporterTests`, mirroring `MermaidExporterTests`' exact-text-match
  style, one full scenario per provider:
  - A simple two-entity one-to-many with a FK, verifying full statement text
    including topological ordering (principal table before dependent).
  - A three-level TPH hierarchy with a discriminator.
  - The same hierarchy re-run as TPT and as TPC, verifying per-strategy table
    shape.
  - A many-to-many relationship, verifying the join table's composite PK and
    two FK constraints.
  - An entity with a check constraint, a computed column, and a unique
    index, verifying all three render correctly per dialect.
  - A view-mapped and a function-mapped entity, verifying neither produces a
    `CREATE TABLE`.

## Non-Goals

- Synthesizing an owner FK/ordinal column for standalone `OwnsMany` tables
  (not modeled in `RelationshipModel.Owned` today).
- Table splitting (`EntityModel.SplitTables`) — one table per entity name.
- `DROP TABLE`/migration-diff generation — this produces a from-scratch
  schema only, not an upgrade script.
- Any dialect beyond SQL Server, PostgreSQL, SQLite.
- Schema support for SQLite (the dialect itself has none).
- A UI diagnostic when SQLite silently drops an entity's schema — the
  exporter is pure and stateless, with no diagnostic channel back to the UI
  today.
