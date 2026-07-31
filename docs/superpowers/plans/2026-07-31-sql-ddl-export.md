# SQL DDL Export Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an "Export SQL" button that downloads dialect-specific `CREATE TABLE`/`ALTER TABLE` DDL generated purely from the parsed `DiagramModelResult`, for SQL Server, PostgreSQL, and SQLite.

**Architecture:** Two new pure-function static classes in `EfSchemaVisualizer.Web.Diagram` — `SqlColumnTypeMapper` (CLR type → dialect column-definition text) and `SqlDdlExporter` (table/constraint/FK assembly, the public entry point) — mirroring the existing `MermaidExporter`/`SvgExporter` shape: no dependency on the live `BlazorDiagram`, string in, string out. Wired into `Home.razor` the same way `ExportMermaidAsync` already is.

**Tech Stack:** C# / .NET 10, xUnit, Blazor WebAssembly (existing project stack — no new dependencies).

## Global Constraints

- Reuse the existing `ScaffoldProvider` enum (`EfSchemaVisualizer.Core.Archive.ScaffoldProvider`: `SqlServer`, `PostgreSql`, `Sqlite`) for dialect selection — do not define a new enum.
- `SqlDdlExporter.Export` and `SqlColumnTypeMapper.MapType` are pure functions: `(DiagramModelResult, ScaffoldProvider) -> string` and `(PropertyModel, ScaffoldProvider) -> string` respectively. No I/O, no `BlazorDiagram` dependency.
- Entities with `ViewName is not null` or `FunctionName is not null` never produce a `CREATE TABLE`.
- Statement order in the output: `CREATE SEQUENCE` block, then `CREATE TABLE` block (FK-safe topological order), then `CREATE INDEX` block, then `ALTER TABLE ... ADD CONSTRAINT ... FOREIGN KEY` block.
- No owner-FK/ordinal-column synthesis for standalone `OwnsMany` entities; no table-splitting support; no migration-diff/`DROP TABLE` generation; SQLite gets no schema qualification. These are documented non-goals from the spec — do not attempt them.
- Full test suite (`dotnet test` at repo root) must stay green after every task.

---

### Task 1: `SqlColumnTypeMapper` — CLR type → SQL type text

**Files:**
- Create: `src/EfSchemaVisualizer.Web/Diagram/SqlColumnTypeMapper.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/Diagram/SqlColumnTypeMapperTests.cs`

**Interfaces:**
- Produces: `internal static class SqlColumnTypeMapper { public static string MapType(PropertyModel property, ScaffoldProvider provider); }` — returns only the type token (e.g. `"nvarchar(100)"`), no nullability/default/identity text. Later tasks call this to build a full column definition.

This class does NOT need `InternalsVisibleTo` added — `EfSchemaVisualizer.Web/Diagram/DiagramAutoLayout.cs:4` already declares
`[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("EfSchemaVisualizer.Web.Tests")]` for this assembly, so `internal` members are already visible to the test project.

- [ ] **Step 1: Write the failing tests**

Create `tests/EfSchemaVisualizer.Web.Tests/Diagram/SqlColumnTypeMapperTests.cs`:

```csharp
using EfSchemaVisualizer.Core.Archive;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Web.Diagram;

namespace EfSchemaVisualizer.Web.Tests.Diagram;

public class SqlColumnTypeMapperTests
{
    private static PropertyModel Prop(
        string clrType,
        int? maxLength = null,
        int? precision = null,
        int? scale = null,
        bool? isUnicode = null,
        bool? isFixedLength = null,
        string? columnType = null,
        bool isEnumType = false,
        string? enumUnderlyingClrType = null) =>
        new("Value", clrType, IsNullable: true, maxLength,
            ColumnType: columnType, Precision: precision, Scale: scale,
            IsUnicode: isUnicode, IsFixedLength: isFixedLength,
            IsEnumType: isEnumType, EnumUnderlyingClrType: enumUnderlyingClrType);

    [Theory]
    [InlineData("int", ScaffoldProvider.SqlServer, "int")]
    [InlineData("int", ScaffoldProvider.PostgreSql, "integer")]
    [InlineData("int", ScaffoldProvider.Sqlite, "INTEGER")]
    [InlineData("long", ScaffoldProvider.SqlServer, "bigint")]
    [InlineData("long", ScaffoldProvider.PostgreSql, "bigint")]
    [InlineData("long", ScaffoldProvider.Sqlite, "INTEGER")]
    [InlineData("short", ScaffoldProvider.SqlServer, "smallint")]
    [InlineData("short", ScaffoldProvider.PostgreSql, "smallint")]
    [InlineData("short", ScaffoldProvider.Sqlite, "INTEGER")]
    [InlineData("byte", ScaffoldProvider.SqlServer, "tinyint")]
    [InlineData("byte", ScaffoldProvider.PostgreSql, "smallint")]
    [InlineData("byte", ScaffoldProvider.Sqlite, "INTEGER")]
    [InlineData("bool", ScaffoldProvider.SqlServer, "bit")]
    [InlineData("bool", ScaffoldProvider.PostgreSql, "boolean")]
    [InlineData("bool", ScaffoldProvider.Sqlite, "INTEGER")]
    [InlineData("double", ScaffoldProvider.SqlServer, "float")]
    [InlineData("double", ScaffoldProvider.PostgreSql, "double precision")]
    [InlineData("double", ScaffoldProvider.Sqlite, "REAL")]
    [InlineData("float", ScaffoldProvider.SqlServer, "real")]
    [InlineData("float", ScaffoldProvider.PostgreSql, "real")]
    [InlineData("float", ScaffoldProvider.Sqlite, "REAL")]
    [InlineData("Guid", ScaffoldProvider.SqlServer, "uniqueidentifier")]
    [InlineData("Guid", ScaffoldProvider.PostgreSql, "uuid")]
    [InlineData("Guid", ScaffoldProvider.Sqlite, "TEXT")]
    [InlineData("DateTime", ScaffoldProvider.SqlServer, "datetime2")]
    [InlineData("DateTime", ScaffoldProvider.PostgreSql, "timestamp")]
    [InlineData("DateTime", ScaffoldProvider.Sqlite, "TEXT")]
    [InlineData("DateTimeOffset", ScaffoldProvider.SqlServer, "datetimeoffset")]
    [InlineData("DateTimeOffset", ScaffoldProvider.PostgreSql, "timestamptz")]
    [InlineData("DateTimeOffset", ScaffoldProvider.Sqlite, "TEXT")]
    [InlineData("DateOnly", ScaffoldProvider.SqlServer, "date")]
    [InlineData("DateOnly", ScaffoldProvider.PostgreSql, "date")]
    [InlineData("DateOnly", ScaffoldProvider.Sqlite, "TEXT")]
    [InlineData("TimeOnly", ScaffoldProvider.SqlServer, "time")]
    [InlineData("TimeSpan", ScaffoldProvider.PostgreSql, "time")]
    public void MapType_SimpleClrTypes_MapsToExpectedSqlType(string clrType, ScaffoldProvider provider, string expected)
    {
        Assert.Equal(expected, SqlColumnTypeMapper.MapType(Prop(clrType), provider));
    }

    [Theory]
    [InlineData(ScaffoldProvider.SqlServer, "decimal(18,2)")]
    [InlineData(ScaffoldProvider.PostgreSql, "numeric")]
    [InlineData(ScaffoldProvider.Sqlite, "NUMERIC")]
    public void MapType_DecimalWithNoPrecisionOrScale_UsesDialectDefault(ScaffoldProvider provider, string expected)
    {
        Assert.Equal(expected, SqlColumnTypeMapper.MapType(Prop("decimal"), provider));
    }

    [Theory]
    [InlineData(ScaffoldProvider.SqlServer, "decimal(10,3)")]
    [InlineData(ScaffoldProvider.PostgreSql, "numeric(10,3)")]
    [InlineData(ScaffoldProvider.Sqlite, "NUMERIC")]
    public void MapType_DecimalWithExplicitPrecisionAndScale_UsesThem(ScaffoldProvider provider, string expected)
    {
        Assert.Equal(expected, SqlColumnTypeMapper.MapType(Prop("decimal", precision: 10, scale: 3), provider));
    }

    [Fact]
    public void MapType_StringWithNoMaxLength_SqlServer_UsesNvarcharMax()
    {
        Assert.Equal("nvarchar(max)", SqlColumnTypeMapper.MapType(Prop("string"), ScaffoldProvider.SqlServer));
    }

    [Fact]
    public void MapType_StringWithMaxLength_SqlServer_UsesBoundedNvarchar()
    {
        Assert.Equal("nvarchar(100)", SqlColumnTypeMapper.MapType(Prop("string", maxLength: 100), ScaffoldProvider.SqlServer));
    }

    [Fact]
    public void MapType_StringWithIsUnicodeFalse_SqlServer_UsesVarchar()
    {
        Assert.Equal("varchar(50)", SqlColumnTypeMapper.MapType(Prop("string", maxLength: 50, isUnicode: false), ScaffoldProvider.SqlServer));
    }

    [Fact]
    public void MapType_StringWithIsFixedLengthTrue_SqlServer_UsesNchar()
    {
        Assert.Equal("nchar(10)", SqlColumnTypeMapper.MapType(Prop("string", maxLength: 10, isFixedLength: true), ScaffoldProvider.SqlServer));
    }

    [Fact]
    public void MapType_StringWithIsUnicodeFalseAndIsFixedLengthTrue_SqlServer_UsesChar()
    {
        Assert.Equal("char(10)", SqlColumnTypeMapper.MapType(
            Prop("string", maxLength: 10, isUnicode: false, isFixedLength: true), ScaffoldProvider.SqlServer));
    }

    [Fact]
    public void MapType_StringWithNoMaxLength_Postgres_UsesText()
    {
        Assert.Equal("text", SqlColumnTypeMapper.MapType(Prop("string"), ScaffoldProvider.PostgreSql));
    }

    [Fact]
    public void MapType_StringWithMaxLength_Postgres_UsesVarchar()
    {
        Assert.Equal("varchar(100)", SqlColumnTypeMapper.MapType(Prop("string", maxLength: 100), ScaffoldProvider.PostgreSql));
    }

    [Fact]
    public void MapType_StringWithMaxLengthAndFixedLength_Postgres_UsesChar()
    {
        Assert.Equal("char(10)", SqlColumnTypeMapper.MapType(Prop("string", maxLength: 10, isFixedLength: true), ScaffoldProvider.PostgreSql));
    }

    [Fact]
    public void MapType_String_Sqlite_AlwaysText()
    {
        Assert.Equal("TEXT", SqlColumnTypeMapper.MapType(Prop("string", maxLength: 100), ScaffoldProvider.Sqlite));
    }

    [Theory]
    [InlineData(ScaffoldProvider.SqlServer, "varbinary(max)")]
    [InlineData(ScaffoldProvider.PostgreSql, "bytea")]
    [InlineData(ScaffoldProvider.Sqlite, "BLOB")]
    public void MapType_ByteArrayWithNoMaxLength_UsesDialectDefault(ScaffoldProvider provider, string expected)
    {
        Assert.Equal(expected, SqlColumnTypeMapper.MapType(Prop("byte[]"), provider));
    }

    [Fact]
    public void MapType_ByteArrayWithMaxLength_SqlServer_UsesBoundedVarbinary()
    {
        Assert.Equal("varbinary(16)", SqlColumnTypeMapper.MapType(Prop("byte[]", maxLength: 16), ScaffoldProvider.SqlServer));
    }

    [Fact]
    public void MapType_ExplicitColumnType_WinsOverEverything()
    {
        Assert.Equal("money", SqlColumnTypeMapper.MapType(Prop("decimal", columnType: "money"), ScaffoldProvider.SqlServer));
    }

    [Fact]
    public void MapType_EnumTypeWithNoConversion_UsesUnderlyingClrType()
    {
        Assert.Equal("tinyint", SqlColumnTypeMapper.MapType(
            Prop("Status", isEnumType: true, enumUnderlyingClrType: "byte"), ScaffoldProvider.SqlServer));
    }

    [Fact]
    public void MapType_UnrecognizedClrType_FallsBackToWidestStringType()
    {
        Assert.Equal("nvarchar(max)", SqlColumnTypeMapper.MapType(Prop("SomeUnknownType"), ScaffoldProvider.SqlServer));
        Assert.Equal("text", SqlColumnTypeMapper.MapType(Prop("SomeUnknownType"), ScaffoldProvider.PostgreSql));
        Assert.Equal("TEXT", SqlColumnTypeMapper.MapType(Prop("SomeUnknownType"), ScaffoldProvider.Sqlite));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter SqlColumnTypeMapperTests`
Expected: FAIL (compile error — `SqlColumnTypeMapper` does not exist yet).

- [ ] **Step 3: Implement `SqlColumnTypeMapper`**

Create `src/EfSchemaVisualizer.Web/Diagram/SqlColumnTypeMapper.cs`:

```csharp
using EfSchemaVisualizer.Core.Archive;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Web.Diagram;

/// <summary>
/// Maps a parsed <see cref="PropertyModel"/> to a dialect-specific SQL column type token
/// (e.g. <c>"nvarchar(100)"</c>) for <see cref="SqlDdlExporter"/>. An explicit
/// <see cref="PropertyModel.ColumnType"/> (from <c>HasColumnType</c>) always wins outright,
/// since it's already a raw SQL type string.
/// </summary>
internal static class SqlColumnTypeMapper
{
    public static string MapType(PropertyModel property, ScaffoldProvider provider)
    {
        if (!string.IsNullOrWhiteSpace(property.ColumnType))
        {
            return property.ColumnType!;
        }

        var clrType = property.IsEnumType
            ? property.EnumUnderlyingClrType ?? "int"
            : property.ClrType.TrimEnd('?');

        return provider switch
        {
            ScaffoldProvider.SqlServer => MapSqlServer(clrType, property),
            ScaffoldProvider.PostgreSql => MapPostgres(clrType, property),
            ScaffoldProvider.Sqlite => MapSqlite(clrType),
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
    }

    private static string MapSqlServer(string clrType, PropertyModel property) => clrType switch
    {
        "int" => "int",
        "long" => "bigint",
        "short" => "smallint",
        "byte" => "tinyint",
        "bool" => "bit",
        "decimal" => $"decimal({property.Precision ?? 18},{property.Scale ?? 2})",
        "double" => "float",
        "float" => "real",
        "string" => SqlServerStringType(property),
        "Guid" => "uniqueidentifier",
        "DateTime" => "datetime2",
        "DateTimeOffset" => "datetimeoffset",
        "DateOnly" => "date",
        "TimeOnly" or "TimeSpan" => "time",
        "byte[]" => property.MaxLength is int n ? $"varbinary({n})" : "varbinary(max)",
        _ => "nvarchar(max)",
    };

    private static string SqlServerStringType(PropertyModel property)
    {
        var basePrefix = property.IsUnicode == false ? "" : "n";
        var kind = property.IsFixedLength == true ? "char" : "varchar";
        var length = property.MaxLength is int n ? n.ToString() : "max";
        return kind == "char" && length == "max" ? $"{basePrefix}varchar(max)" : $"{basePrefix}{kind}({length})";
    }

    private static string MapPostgres(string clrType, PropertyModel property) => clrType switch
    {
        "int" => "integer",
        "long" => "bigint",
        "short" => "smallint",
        "byte" => "smallint",
        "bool" => "boolean",
        "decimal" => property.Precision is int p ? $"numeric({p},{property.Scale ?? 0})" : "numeric",
        "double" => "double precision",
        "float" => "real",
        "string" => PostgresStringType(property),
        "Guid" => "uuid",
        "DateTime" => "timestamp",
        "DateTimeOffset" => "timestamptz",
        "DateOnly" => "date",
        "TimeOnly" or "TimeSpan" => "time",
        "byte[]" => "bytea",
        _ => "text",
    };

    private static string PostgresStringType(PropertyModel property)
    {
        if (property.MaxLength is not int n)
        {
            return "text";
        }

        return property.IsFixedLength == true ? $"char({n})" : $"varchar({n})";
    }

    private static string MapSqlite(string clrType) => clrType switch
    {
        "int" or "long" or "short" or "byte" or "bool" => "INTEGER",
        "double" or "float" => "REAL",
        "decimal" => "NUMERIC",
        "byte[]" => "BLOB",
        _ => "TEXT",
    };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter SqlColumnTypeMapperTests`
Expected: PASS, all cases green.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/SqlColumnTypeMapper.cs tests/EfSchemaVisualizer.Web.Tests/Diagram/SqlColumnTypeMapperTests.cs
git commit -m "Add SqlColumnTypeMapper for CLR-to-SQL type mapping across three dialects"
```

---

### Task 2: Identifier quoting and physical table naming

**Files:**
- Create: `src/EfSchemaVisualizer.Web/Diagram/SqlDdlExporter.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/Diagram/SqlDdlExporterTests.cs`

**Interfaces:**
- Consumes: `SqlColumnTypeMapper.MapType` (Task 1).
- Produces: `internal static string QuoteIdentifier(string name, ScaffoldProvider provider)`, `internal static string PhysicalTableName(EntityModel entity)` (returns `entity.TableName ?? entity.Name` — documented limitation: no DbSet pluralization data is available in `DiagramModelResult`, so the convention-based table name is the bare entity name, not a pluralized DbSet name), `internal static string QualifiedTableName(EntityModel entity, ScaffoldProvider provider)` (applies schema qualification; SQLite never qualifies). These are `internal static` members of the same `SqlDdlExporter` class that Task 8 will add the public `Export` method to.

- [ ] **Step 1: Write the failing tests**

Create `tests/EfSchemaVisualizer.Web.Tests/Diagram/SqlDdlExporterTests.cs`:

```csharp
using EfSchemaVisualizer.Core.Archive;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Web.Diagram;

namespace EfSchemaVisualizer.Web.Tests.Diagram;

public class SqlDdlExporterTests
{
    private static EntityModel Entity(string name, params PropertyModel[] properties) =>
        new(name, properties);

    [Theory]
    [InlineData(ScaffoldProvider.SqlServer, "[Name]")]
    [InlineData(ScaffoldProvider.PostgreSql, "\"Name\"")]
    [InlineData(ScaffoldProvider.Sqlite, "\"Name\"")]
    public void QuoteIdentifier_QuotesPerDialect(ScaffoldProvider provider, string expected)
    {
        Assert.Equal(expected, SqlDdlExporter.QuoteIdentifier("Name", provider));
    }

    [Fact]
    public void PhysicalTableName_UsesExplicitTableNameWhenSet()
    {
        var entity = Entity("Blog") with { TableName = "Blogs" };
        Assert.Equal("Blogs", SqlDdlExporter.PhysicalTableName(entity));
    }

    [Fact]
    public void PhysicalTableName_FallsBackToEntityNameWhenTableNameNotSet()
    {
        Assert.Equal("Blog", SqlDdlExporter.PhysicalTableName(Entity("Blog")));
    }

    [Theory]
    [InlineData(ScaffoldProvider.SqlServer, "[sales].[Order]")]
    [InlineData(ScaffoldProvider.PostgreSql, "\"sales\".\"Order\"")]
    public void QualifiedTableName_WithSchema_QualifiesPerDialect(ScaffoldProvider provider, string expected)
    {
        var entity = Entity("Order") with { Schema = "sales" };
        Assert.Equal(expected, SqlDdlExporter.QualifiedTableName(entity, provider));
    }

    [Fact]
    public void QualifiedTableName_Sqlite_NeverQualifiesWithSchema()
    {
        var entity = Entity("Order") with { Schema = "sales" };
        Assert.Equal("\"Order\"", SqlDdlExporter.QualifiedTableName(entity, ScaffoldProvider.Sqlite));
    }

    [Theory]
    [InlineData(ScaffoldProvider.SqlServer, "[Order]")]
    [InlineData(ScaffoldProvider.PostgreSql, "\"Order\"")]
    public void QualifiedTableName_WithNoSchema_OmitsSchemaPrefix(ScaffoldProvider provider, string expected)
    {
        Assert.Equal(expected, SqlDdlExporter.QualifiedTableName(Entity("Order"), provider));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter SqlDdlExporterTests`
Expected: FAIL (compile error — `SqlDdlExporter` does not exist yet).

- [ ] **Step 3: Implement quoting/naming helpers**

Create `src/EfSchemaVisualizer.Web/Diagram/SqlDdlExporter.cs`:

```csharp
using System.Text;
using EfSchemaVisualizer.Core.Archive;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Web.Diagram;

/// <summary>
/// Renders a <see cref="DiagramModelResult"/> as dialect-specific SQL DDL text (SQL Server /
/// PostgreSQL / SQLite). Pure string generation from the parsed model, same posture as
/// <see cref="MermaidExporter"/> — no dependency on the live <c>BlazorDiagram</c>.
/// </summary>
public static class SqlDdlExporter
{
    internal static string QuoteIdentifier(string name, ScaffoldProvider provider) => provider switch
    {
        ScaffoldProvider.SqlServer => $"[{name}]",
        _ => $"\"{name}\"",
    };

    internal static string PhysicalTableName(EntityModel entity) => entity.TableName ?? entity.Name;

    internal static string QualifiedTableName(EntityModel entity, ScaffoldProvider provider)
    {
        var table = QuoteIdentifier(PhysicalTableName(entity), provider);

        if (provider == ScaffoldProvider.Sqlite || entity.Schema is null)
        {
            return table;
        }

        return $"{QuoteIdentifier(entity.Schema, provider)}.{table}";
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter SqlDdlExporterTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/SqlDdlExporter.cs tests/EfSchemaVisualizer.Web.Tests/Diagram/SqlDdlExporterTests.cs
git commit -m "Add SqlDdlExporter identifier quoting and table naming helpers"
```

---

### Task 3: Column definition rendering (nullability, defaults, computed columns, identity)

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/SqlDdlExporter.cs`
- Modify: `tests/EfSchemaVisualizer.Web.Tests/Diagram/SqlDdlExporterTests.cs`

**Interfaces:**
- Consumes: `SqlColumnTypeMapper.MapType` (Task 1), `QuoteIdentifier` (Task 2).
- Produces: `internal static string RenderColumnDefinition(PropertyModel property, bool isSoleIntegerIdentityPrimaryKey, ScaffoldProvider provider)`. The `isSoleIntegerIdentityPrimaryKey` flag is computed by the caller (Task 5) — this method does not itself decide PK-ness, only how to render identity syntax once told a column is one. Returns the full column line text with no trailing comma/newline (caller joins with `,\n`).
- `internal static bool IsIdentityCandidate(PropertyModel property)` — true when `property.ValueGenerated != "Never"` and the CLR type (post `TrimEnd('?')`) is one of `int`/`long`/`short`/`byte`. This captures the EF convention that an integer-typed property with no explicit `ValueGeneratedNever` is value-generated on add by default, not just properties with an explicit `ValueGeneratedOnAdd`/`UseIdentityColumn` call.

- [ ] **Step 1: Write the failing tests**

Append to `tests/EfSchemaVisualizer.Web.Tests/Diagram/SqlDdlExporterTests.cs` (inside the existing class):

```csharp
    private static PropertyModel Column(
        string name,
        string clrType = "string",
        bool isNullable = true,
        int? maxLength = null,
        string? defaultValueLiteral = null,
        string? defaultValueSql = null,
        string? computedColumnSql = null,
        bool? computedColumnSqlIsStored = null,
        string? valueGenerated = null) =>
        new(name, clrType, isNullable, maxLength,
            DefaultValueLiteral: defaultValueLiteral, DefaultValueSql: defaultValueSql,
            ComputedColumnSql: computedColumnSql, ComputedColumnSqlIsStored: computedColumnSqlIsStored,
            ValueGenerated: valueGenerated);

    [Fact]
    public void RenderColumnDefinition_NullableProperty_EmitsNull()
    {
        var line = SqlDdlExporter.RenderColumnDefinition(Column("Nickname", isNullable: true), false, ScaffoldProvider.SqlServer);
        Assert.Equal("[Nickname] nvarchar(max) NULL", line);
    }

    [Fact]
    public void RenderColumnDefinition_NonNullableProperty_EmitsNotNull()
    {
        var line = SqlDdlExporter.RenderColumnDefinition(Column("Name", isNullable: false, maxLength: 100), false, ScaffoldProvider.SqlServer);
        Assert.Equal("[Name] nvarchar(100) NOT NULL", line);
    }

    [Fact]
    public void RenderColumnDefinition_DefaultValueLiteral_AppendsDefaultClause()
    {
        var line = SqlDdlExporter.RenderColumnDefinition(
            Column("Status", clrType: "int", isNullable: false, defaultValueLiteral: "1"), false, ScaffoldProvider.SqlServer);
        Assert.Equal("[Status] int NOT NULL DEFAULT 1", line);
    }

    [Fact]
    public void RenderColumnDefinition_DefaultValueSql_AppendsDefaultWithParens()
    {
        var line = SqlDdlExporter.RenderColumnDefinition(
            Column("CreatedAt", clrType: "DateTime", isNullable: false, defaultValueSql: "GETUTCDATE()"), false, ScaffoldProvider.SqlServer);
        Assert.Equal("[CreatedAt] datetime2 NOT NULL DEFAULT (GETUTCDATE())", line);
    }

    [Theory]
    [InlineData(ScaffoldProvider.SqlServer, "[Total] int NOT NULL AS ([Price] * [Qty]) PERSISTED")]
    [InlineData(ScaffoldProvider.PostgreSql, "\"Total\" int GENERATED ALWAYS AS ([Price] * [Qty]) STORED")]
    [InlineData(ScaffoldProvider.Sqlite, "\"Total\" INTEGER GENERATED ALWAYS AS ([Price] * [Qty]) STORED")]
    public void RenderColumnDefinition_ComputedColumnStored_EmitsPerDialectSyntax(ScaffoldProvider provider, string expected)
    {
        var line = SqlDdlExporter.RenderColumnDefinition(
            Column("Total", clrType: "int", isNullable: false, computedColumnSql: "[Price] * [Qty]", computedColumnSqlIsStored: true),
            false, provider);
        Assert.Equal(expected, line);
    }

    [Fact]
    public void RenderColumnDefinition_ComputedColumnNotStored_SqlServer_OmitsPersisted()
    {
        var line = SqlDdlExporter.RenderColumnDefinition(
            Column("Total", clrType: "int", isNullable: false, computedColumnSql: "[Price] * [Qty]", computedColumnSqlIsStored: false),
            false, ScaffoldProvider.SqlServer);
        Assert.Equal("[Total] int NOT NULL AS ([Price] * [Qty])", line);
    }

    [Theory]
    [InlineData(ScaffoldProvider.SqlServer, "[Id] int IDENTITY(1,1) NOT NULL")]
    [InlineData(ScaffoldProvider.PostgreSql, "\"Id\" integer GENERATED ALWAYS AS IDENTITY NOT NULL")]
    public void RenderColumnDefinition_SoleIntegerIdentityPrimaryKey_EmitsIdentitySyntax(ScaffoldProvider provider, string expected)
    {
        var line = SqlDdlExporter.RenderColumnDefinition(Column("Id", clrType: "int", isNullable: false), true, provider);
        Assert.Equal(expected, line);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("Never", false)]
    [InlineData("OnAdd", true)]
    [InlineData("Identity", true)]
    public void IsIdentityCandidate_IntegerType_RespectsValueGeneratedNever(string? valueGenerated, bool expected)
    {
        Assert.Equal(expected, SqlDdlExporter.IsIdentityCandidate(Column("Id", clrType: "int", valueGenerated: valueGenerated)));
    }

    [Fact]
    public void IsIdentityCandidate_NonIntegerType_IsFalse()
    {
        Assert.False(SqlDdlExporter.IsIdentityCandidate(Column("Id", clrType: "Guid")));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter SqlDdlExporterTests`
Expected: FAIL (compile error — `RenderColumnDefinition`/`IsIdentityCandidate` don't exist yet).

- [ ] **Step 3: Implement column rendering**

Add to `src/EfSchemaVisualizer.Web/Diagram/SqlDdlExporter.cs` (inside the `SqlDdlExporter` class):

```csharp
    private static readonly HashSet<string> IntegerClrTypes = new() { "int", "long", "short", "byte" };

    internal static bool IsIdentityCandidate(PropertyModel property) =>
        property.ValueGenerated != "Never" && IntegerClrTypes.Contains(property.ClrType.TrimEnd('?'));

    internal static string RenderColumnDefinition(PropertyModel property, bool isSoleIntegerIdentityPrimaryKey, ScaffoldProvider provider)
    {
        var name = QuoteIdentifier(property.ColumnName ?? property.Name, provider);
        var sqlType = SqlColumnTypeMapper.MapType(property, provider);
        var nullability = property.IsNullable && property.IsRequiredOverride != true ? "NULL" : "NOT NULL";

        if (property.ComputedColumnSql is not null)
        {
            return RenderComputedColumn(name, sqlType, nullability, property, provider);
        }

        var identity = isSoleIntegerIdentityPrimaryKey ? IdentityClause(provider) : "";
        var defaultClause = RenderDefaultClause(property);

        return $"{name} {sqlType}{identity} {nullability}{defaultClause}";
    }

    private static string RenderComputedColumn(string name, string sqlType, string nullability, PropertyModel property, ScaffoldProvider provider)
    {
        return provider switch
        {
            ScaffoldProvider.SqlServer => property.ComputedColumnSqlIsStored != false
                ? $"{name} {sqlType} {nullability} AS ({property.ComputedColumnSql}) PERSISTED"
                : $"{name} {sqlType} {nullability} AS ({property.ComputedColumnSql})",
            _ => $"{name} {sqlType} GENERATED ALWAYS AS ({property.ComputedColumnSql}) STORED",
        };
    }

    private static string IdentityClause(ScaffoldProvider provider) => provider switch
    {
        ScaffoldProvider.SqlServer => " IDENTITY(1,1)",
        ScaffoldProvider.PostgreSql => " GENERATED ALWAYS AS IDENTITY",
        _ => "",
    };

    private static string RenderDefaultClause(PropertyModel property)
    {
        if (property.DefaultValueLiteral is not null)
        {
            return $" DEFAULT {property.DefaultValueLiteral}";
        }

        if (property.DefaultValueSql is not null)
        {
            return $" DEFAULT ({property.DefaultValueSql})";
        }

        return "";
    }
```

The SQL Server computed-column branch includes the `nullability` token before `AS` (matching the literal expected string in Step 1: `"[Total] int NOT NULL AS ([Price] * [Qty]) PERSISTED"`), while the PostgreSQL/SQLite branch omits it (`GENERATED ALWAYS AS (...) STORED` carries no separate nullability clause in the expected strings). Treat the literal strings in Step 1's test bodies as authoritative for exact spacing/token order.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter SqlDdlExporterTests`
Expected: PASS. If the computed-column or identity assertions fail, adjust `RenderComputedColumn`/`IdentityClause`/`RenderColumnDefinition` string interpolation (spacing, token order) until the exact literal strings from Step 1 match — those tests are the source of truth for formatting.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/SqlDdlExporter.cs tests/EfSchemaVisualizer.Web.Tests/Diagram/SqlDdlExporterTests.cs
git commit -m "Add SqlDdlExporter column definition rendering (defaults, computed columns, identity)"
```

---

### Task 4: Entity selection and FK-safe table ordering

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/SqlDdlExporter.cs`
- Modify: `tests/EfSchemaVisualizer.Web.Tests/Diagram/SqlDdlExporterTests.cs`

**Interfaces:**
- Consumes: `EfSchemaVisualizer.Web.Diagram.DiagramAutoLayout.ComputeLayers(IReadOnlyList<EntityModel>, IReadOnlyList<RelationshipModel>)` (existing, returns `Dictionary<string, int>` of entity name → layer, principals in earlier layers).
- Produces: `internal static List<EntityModel> SelectPhysicalEntities(IReadOnlyList<EntityModel> entities)` (drops `ViewName is not null` and `FunctionName is not null` entities), `internal static List<EntityModel> OrderTablesByDependency(IReadOnlyList<EntityModel> physicalEntities, IReadOnlyList<RelationshipModel> relationships)` (stable sort by `ComputeLayers` value ascending, ties broken by original list order).

- [ ] **Step 1: Write the failing tests**

Append to `tests/EfSchemaVisualizer.Web.Tests/Diagram/SqlDdlExporterTests.cs`:

```csharp
    [Fact]
    public void SelectPhysicalEntities_ExcludesViewMappedAndFunctionMappedEntities()
    {
        var table = Entity("Order");
        var view = Entity("OrderSummary") with { ViewName = "vw_OrderSummary" };
        var function = Entity("ActiveOrders") with { FunctionName = "fn_ActiveOrders" };

        var result = SqlDdlExporter.SelectPhysicalEntities(new[] { table, view, function });

        Assert.Equal(new[] { "Order" }, result.Select(e => e.Name));
    }

    [Fact]
    public void OrderTablesByDependency_PrincipalBeforeDependent()
    {
        var blog = Entity("Blog", Column("Id", "int", isNullable: false));
        var post = Entity("Post", Column("Id", "int", isNullable: false), Column("BlogId", "int", isNullable: false));
        var relationship = new RelationshipModel(
            "Blog", "Post", RelationshipKind.OneToMany, PrincipalNavigation: null, DependentNavigation: null,
            ForeignKeyProperties: new[] { "BlogId" });

        var ordered = SqlDdlExporter.OrderTablesByDependency(new[] { post, blog }, new[] { relationship });

        Assert.Equal(new[] { "Blog", "Post" }, ordered.Select(e => e.Name));
    }

    [Fact]
    public void OrderTablesByDependency_NoRelationships_PreservesOriginalOrder()
    {
        var a = Entity("A");
        var b = Entity("B");

        var ordered = SqlDdlExporter.OrderTablesByDependency(new[] { a, b }, Array.Empty<RelationshipModel>());

        Assert.Equal(new[] { "A", "B" }, ordered.Select(e => e.Name));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter SqlDdlExporterTests`
Expected: FAIL (compile error — methods don't exist yet).

- [ ] **Step 3: Implement selection and ordering**

Add to `src/EfSchemaVisualizer.Web/Diagram/SqlDdlExporter.cs`:

```csharp
    internal static List<EntityModel> SelectPhysicalEntities(IReadOnlyList<EntityModel> entities) =>
        entities.Where(e => e.ViewName is null && e.FunctionName is null).ToList();

    internal static List<EntityModel> OrderTablesByDependency(
        IReadOnlyList<EntityModel> physicalEntities, IReadOnlyList<RelationshipModel> relationships)
    {
        var layers = DiagramAutoLayout.ComputeLayers(physicalEntities, relationships);

        return physicalEntities
            .Select((entity, index) => (entity, index))
            .OrderBy(t => layers.GetValueOrDefault(t.entity.Name, 0))
            .ThenBy(t => t.index)
            .Select(t => t.entity)
            .ToList();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter SqlDdlExporterTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/SqlDdlExporter.cs tests/EfSchemaVisualizer.Web.Tests/Diagram/SqlDdlExporterTests.cs
git commit -m "Add SqlDdlExporter physical-table selection and FK-safe ordering"
```

---

### Task 5: `CREATE TABLE` for a plain (non-hierarchy) entity

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/SqlDdlExporter.cs`
- Modify: `tests/EfSchemaVisualizer.Web.Tests/Diagram/SqlDdlExporterTests.cs`

**Interfaces:**
- Consumes: `RenderColumnDefinition`, `IsIdentityCandidate` (Task 3), `QualifiedTableName`, `QuoteIdentifier` (Task 2).
- Produces: `internal static string RenderCreateTable(EntityModel entity, IReadOnlyList<string> primaryKeyColumnNames, ScaffoldProvider provider)`. `primaryKeyColumnNames` is passed in by the caller (Task 8's orchestrator) rather than always read from `entity.KeyPropertyNames`, because shared-type join-entity tables (Task 7) need to supply a synthesized composite PK the entity model itself doesn't carry. Pass `entity.KeyPropertyNames` directly for every other case. Handles: `IsKeyless` (no PK clause), SQLite single-integer-identity-PK inline `INTEGER PRIMARY KEY AUTOINCREMENT` special case, `CheckConstraints`, and `AlternateKeys` as `UNIQUE` constraints.

- [ ] **Step 1: Write the failing tests**

Append to `tests/EfSchemaVisualizer.Web.Tests/Diagram/SqlDdlExporterTests.cs`:

```csharp
    [Fact]
    public void RenderCreateTable_SimpleEntity_SqlServer_EmitsColumnsAndPrimaryKey()
    {
        var entity = Entity("Blog",
                Column("Id", "int", isNullable: false),
                Column("Title", "string", isNullable: false, maxLength: 200))
            with { KeyPropertyNames = new[] { "Id" } };

        var sql = SqlDdlExporter.RenderCreateTable(entity, entity.KeyPropertyNames, ScaffoldProvider.SqlServer);

        Assert.Equal(
            "CREATE TABLE [Blog] (\n" +
            "    [Id] int IDENTITY(1,1) NOT NULL,\n" +
            "    [Title] nvarchar(200) NOT NULL,\n" +
            "    CONSTRAINT [PK_Blog] PRIMARY KEY ([Id])\n" +
            ");\n",
            sql);
    }

    [Fact]
    public void RenderCreateTable_ExplicitKeyName_UsesItForConstraintName()
    {
        var entity = Entity("Blog", Column("Id", "int", isNullable: false))
            with { KeyPropertyNames = new[] { "Id" }, KeyName = "PK_MyBlog" };

        var sql = SqlDdlExporter.RenderCreateTable(entity, entity.KeyPropertyNames, ScaffoldProvider.SqlServer);

        Assert.Contains("CONSTRAINT [PK_MyBlog] PRIMARY KEY ([Id])", sql);
    }

    [Fact]
    public void RenderCreateTable_KeylessEntity_EmitsNoPrimaryKeyClause()
    {
        var entity = Entity("Report", Column("Value", "int", isNullable: false)) with { IsKeyless = true };

        var sql = SqlDdlExporter.RenderCreateTable(entity, Array.Empty<string>(), ScaffoldProvider.SqlServer);

        Assert.DoesNotContain("PRIMARY KEY", sql);
    }

    [Fact]
    public void RenderCreateTable_CheckConstraint_IsIncluded()
    {
        var entity = Entity("Order", Column("Total", "decimal", isNullable: false))
            with
            {
                KeyPropertyNames = Array.Empty<string>(),
                IsKeyless = true,
                CheckConstraints = new[] { new CheckConstraintModel("CK_Order_Total", "[Total] >= 0") },
            };

        var sql = SqlDdlExporter.RenderCreateTable(entity, Array.Empty<string>(), ScaffoldProvider.SqlServer);

        Assert.Contains("CONSTRAINT [CK_Order_Total] CHECK ([Total] >= 0)", sql);
    }

    [Fact]
    public void RenderCreateTable_AlternateKey_EmitsUniqueConstraint()
    {
        var entity = Entity("User", Column("Email", "string", isNullable: false, maxLength: 200))
            with
            {
                KeyPropertyNames = Array.Empty<string>(),
                IsKeyless = true,
                AlternateKeys = new IReadOnlyList<string>[] { new[] { "Email" } },
            };

        var sql = SqlDdlExporter.RenderCreateTable(entity, Array.Empty<string>(), ScaffoldProvider.SqlServer);

        Assert.Contains("CONSTRAINT [AK_User_Email] UNIQUE ([Email])", sql);
    }

    [Fact]
    public void RenderCreateTable_Sqlite_SoleIntegerIdentityPrimaryKey_UsesInlineAutoincrement()
    {
        var entity = Entity("Blog",
                Column("Id", "int", isNullable: false),
                Column("Title", "string", isNullable: false))
            with { KeyPropertyNames = new[] { "Id" } };

        var sql = SqlDdlExporter.RenderCreateTable(entity, entity.KeyPropertyNames, ScaffoldProvider.Sqlite);

        Assert.Equal(
            "CREATE TABLE \"Blog\" (\n" +
            "    \"Id\" INTEGER PRIMARY KEY AUTOINCREMENT,\n" +
            "    \"Title\" TEXT NOT NULL\n" +
            ");\n",
            sql);
    }

    [Fact]
    public void RenderCreateTable_Sqlite_CompositePrimaryKey_UsesTrailingConstraintNotInline()
    {
        var entity = Entity("OrderLine",
                Column("OrderId", "int", isNullable: false),
                Column("LineNumber", "int", isNullable: false))
            with { KeyPropertyNames = new[] { "OrderId", "LineNumber" } };

        var sql = SqlDdlExporter.RenderCreateTable(entity, entity.KeyPropertyNames, ScaffoldProvider.Sqlite);

        Assert.Contains("PRIMARY KEY (\"OrderId\", \"LineNumber\")", sql);
        Assert.DoesNotContain("AUTOINCREMENT", sql);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter SqlDdlExporterTests`
Expected: FAIL (compile error — `RenderCreateTable` doesn't exist yet).

- [ ] **Step 3: Implement `RenderCreateTable`**

Add to `src/EfSchemaVisualizer.Web/Diagram/SqlDdlExporter.cs`:

```csharp
    internal static string RenderCreateTable(EntityModel entity, IReadOnlyList<string> primaryKeyColumnNames, ScaffoldProvider provider)
    {
        var sb = new StringBuilder();
        sb.Append("CREATE TABLE ").Append(QualifiedTableName(entity, provider)).Append(" (\n");

        var sqliteInlineAutoIncrement =
            provider == ScaffoldProvider.Sqlite &&
            primaryKeyColumnNames.Count == 1 &&
            entity.Properties.FirstOrDefault(p => p.Name == primaryKeyColumnNames[0]) is { } solePk &&
            IsIdentityCandidate(solePk);

        var lines = new List<string>();

        foreach (var property in entity.Properties)
        {
            var isSolePk = primaryKeyColumnNames.Count == 1 && property.Name == primaryKeyColumnNames[0];

            if (sqliteInlineAutoIncrement && isSolePk)
            {
                var name = QuoteIdentifier(property.ColumnName ?? property.Name, provider);
                lines.Add($"    {name} INTEGER PRIMARY KEY AUTOINCREMENT");
                continue;
            }

            var isIdentityColumn = isSolePk && provider != ScaffoldProvider.Sqlite && IsIdentityCandidate(property);
            lines.Add("    " + RenderColumnDefinition(property, isIdentityColumn, provider));
        }

        if (!entity.IsKeyless && primaryKeyColumnNames.Count > 0 && !sqliteInlineAutoIncrement)
        {
            var keyName = entity.KeyName ?? $"PK_{PhysicalTableName(entity)}";
            var columns = string.Join(", ", primaryKeyColumnNames.Select(c => QuoteIdentifier(c, provider)));
            lines.Add($"    CONSTRAINT {QuoteIdentifier(keyName, provider)} PRIMARY KEY ({columns})");
        }

        foreach (var check in entity.CheckConstraints)
        {
            lines.Add($"    CONSTRAINT {QuoteIdentifier(check.Name, provider)} CHECK ({check.Sql})");
        }

        foreach (var alternateKey in entity.AlternateKeys)
        {
            var akName = $"AK_{PhysicalTableName(entity)}_{string.Join("_", alternateKey)}";
            var columns = string.Join(", ", alternateKey.Select(c => QuoteIdentifier(c, provider)));
            lines.Add($"    CONSTRAINT {QuoteIdentifier(akName, provider)} UNIQUE ({columns})");
        }

        sb.Append(string.Join(",\n", lines));
        sb.Append("\n);\n");
        return sb.ToString();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter SqlDdlExporterTests`
Expected: PASS. If spacing/line-join details differ from the exact expected strings in Step 1, adjust the implementation (not the tests) — those literal strings are the authoritative output format.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/SqlDdlExporter.cs tests/EfSchemaVisualizer.Web.Tests/Diagram/SqlDdlExporterTests.cs
git commit -m "Add SqlDdlExporter CREATE TABLE rendering for plain entities"
```

---

### Task 6: TPH hierarchy — merged root table with discriminator column

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/SqlDdlExporter.cs`
- Modify: `tests/EfSchemaVisualizer.Web.Tests/Diagram/SqlDdlExporterTests.cs`

**Interfaces:**
- Consumes: `RenderCreateTable` (Task 5).
- Produces: `internal static List<EntityModel> CollectDescendants(EntityModel root, IReadOnlyList<EntityModel> allEntities)` (transitive, BFS via `BaseEntityName`, cycle-safe), `internal static EntityModel BuildTphMergedEntity(EntityModel root, IReadOnlyList<EntityModel> allEntities)` — returns a synthetic `EntityModel` (same `Name`/`TableName`/`Schema`/`KeyPropertyNames`/`KeyName` as `root`, but `Properties` = root's own properties plus every descendant's own-declared properties (deduplicated by name, first occurrence wins, each forced `IsNullable = true` since only one subtype's rows populate it) plus a trailing discriminator column named `root.DiscriminatorPropertyName ?? "Discriminator"` typed `root.DiscriminatorClrType ?? "string"`, non-nullable). Only called when `root` has at least one descendant.

Background this task's implementer needs: `EntityModel.BaseEntityName` still points to the direct parent name after `InheritanceInference.Fold` runs (fold does not clear it) — see `src/EfSchemaVisualizer.Core/Inference/InheritanceInference.cs:32-36`. A TPH hierarchy root is any entity with `BaseEntityName is null` (or pointing to an entity not present in `allEntities`). A property counts as "this descendant's own" (as opposed to folded-in from an ancestor) when its `DeclaringEntityName is null` — folded/inherited properties always have `DeclaringEntityName` set to the ancestor that declared them (`InheritanceInference.cs:73,103`), while genuinely own-declared properties retain whatever `DeclaringEntityName` they had before folding, which is `null` for every existing test fixture and parser path in this codebase.

- [ ] **Step 1: Write the failing tests**

Append to `tests/EfSchemaVisualizer.Web.Tests/Diagram/SqlDdlExporterTests.cs`:

```csharp
    [Fact]
    public void CollectDescendants_MultiLevelHierarchy_ReturnsAllTransitiveDescendants()
    {
        var person = Entity("Person");
        var student = Entity("Student") with { BaseEntityName = "Person" };
        var gradStudent = Entity("GradStudent") with { BaseEntityName = "Student" };
        var all = new[] { person, student, gradStudent };

        var descendants = SqlDdlExporter.CollectDescendants(person, all);

        Assert.Equal(new[] { "Student", "GradStudent" }, descendants.Select(e => e.Name));
    }

    [Fact]
    public void BuildTphMergedEntity_UnionsOwnPropertiesFromEveryDescendant()
    {
        var person = Entity("Person",
                Column("Id", "int", isNullable: false),
                Column("Name", "string", isNullable: false))
            with { KeyPropertyNames = new[] { "Id" } };

        var studentOwn = Column("Course", "string", isNullable: false);
        var student = Entity("Student",
            new PropertyModel("Id", "int", false, null, DeclaringEntityName: "Person"),
            new PropertyModel("Name", "string", false, null, DeclaringEntityName: "Person"),
            studentOwn) with { BaseEntityName = "Person", KeyPropertyNames = new[] { "Id" } };

        var teacherOwn = Column("Salary", "decimal", isNullable: false);
        var teacher = Entity("Teacher",
            new PropertyModel("Id", "int", false, null, DeclaringEntityName: "Person"),
            new PropertyModel("Name", "string", false, null, DeclaringEntityName: "Person"),
            teacherOwn) with { BaseEntityName = "Person", KeyPropertyNames = new[] { "Id" } };

        var merged = SqlDdlExporter.BuildTphMergedEntity(person, new[] { person, student, teacher });

        Assert.Equal(new[] { "Id", "Name", "Course", "Salary", "Discriminator" }, merged.Properties.Select(p => p.Name));
        Assert.True(merged.Properties.First(p => p.Name == "Course").IsNullable);
        Assert.True(merged.Properties.First(p => p.Name == "Salary").IsNullable);
        Assert.False(merged.Properties.First(p => p.Name == "Discriminator").IsNullable);
    }

    [Fact]
    public void BuildTphMergedEntity_UsesConfiguredDiscriminatorNameAndType()
    {
        var person = Entity("Person", Column("Id", "int", isNullable: false))
            with { KeyPropertyNames = new[] { "Id" }, DiscriminatorPropertyName = "PersonType", DiscriminatorClrType = "int" };
        var student = Entity("Student", new PropertyModel("Id", "int", false, null, DeclaringEntityName: "Person"))
            with { BaseEntityName = "Person", KeyPropertyNames = new[] { "Id" } };

        var merged = SqlDdlExporter.BuildTphMergedEntity(person, new[] { person, student });

        var discriminator = merged.Properties.Last();
        Assert.Equal("PersonType", discriminator.Name);
        Assert.Equal("int", discriminator.ClrType);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter SqlDdlExporterTests`
Expected: FAIL (compile error — `CollectDescendants`/`BuildTphMergedEntity` don't exist yet).

- [ ] **Step 3: Implement TPH merging**

Add to `src/EfSchemaVisualizer.Web/Diagram/SqlDdlExporter.cs`:

```csharp
    internal static List<EntityModel> CollectDescendants(EntityModel root, IReadOnlyList<EntityModel> allEntities)
    {
        var result = new List<EntityModel>();
        var visited = new HashSet<string> { root.Name };
        var frontier = new Queue<string>();
        frontier.Enqueue(root.Name);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var child in allEntities.Where(e => e.BaseEntityName == current))
            {
                if (visited.Add(child.Name))
                {
                    result.Add(child);
                    frontier.Enqueue(child.Name);
                }
            }
        }

        return result;
    }

    internal static EntityModel BuildTphMergedEntity(EntityModel root, IReadOnlyList<EntityModel> allEntities)
    {
        var columns = new List<PropertyModel>(root.Properties);
        var seenNames = new HashSet<string>(columns.Select(c => c.Name));

        foreach (var descendant in CollectDescendants(root, allEntities))
        {
            foreach (var property in descendant.Properties.Where(p => p.DeclaringEntityName is null))
            {
                if (seenNames.Add(property.Name))
                {
                    columns.Add(property with { IsNullable = true });
                }
            }
        }

        var discriminatorName = root.DiscriminatorPropertyName ?? "Discriminator";
        var discriminatorClrType = root.DiscriminatorClrType ?? "string";
        columns.Add(new PropertyModel(discriminatorName, discriminatorClrType, IsNullable: false, MaxLength: null));

        return root with { Properties = columns };
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter SqlDdlExporterTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/SqlDdlExporter.cs tests/EfSchemaVisualizer.Web.Tests/Diagram/SqlDdlExporterTests.cs
git commit -m "Add SqlDdlExporter TPH merged-table construction with discriminator column"
```

---

### Task 7: Indexes, and entity classification for the orchestrator

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/SqlDdlExporter.cs`
- Modify: `tests/EfSchemaVisualizer.Web.Tests/Diagram/SqlDdlExporterTests.cs`

**Interfaces:**
- Consumes: `QuoteIdentifier`, `QualifiedTableName`, `PhysicalTableName` (Task 2), `CollectDescendants` (Task 6).
- Produces: `internal static string RenderCreateIndex(EntityModel entity, IndexModel index, ScaffoldProvider provider)`, `internal static bool IsSkippedTphMember(EntityModel entity, IReadOnlyList<EntityModel> allEntities)` (true when `entity.MappingStrategy == MappingStrategy.Tph` and `entity.BaseEntityName` resolves to another entity in `allEntities` — i.e. this is a non-root TPH hierarchy member that must NOT get its own table, since Task 8's orchestrator emits one merged table for the root instead).

- [ ] **Step 1: Write the failing tests**

Append to `tests/EfSchemaVisualizer.Web.Tests/Diagram/SqlDdlExporterTests.cs`:

```csharp
    [Fact]
    public void RenderCreateIndex_WithExplicitName_UsesIt()
    {
        var entity = Entity("Order", Column("Total", "decimal", isNullable: false));
        var index = new IndexModel(new[] { "Total" }, IsUnique: false, Name: "IX_Order_Total");

        var sql = SqlDdlExporter.RenderCreateIndex(entity, index, ScaffoldProvider.SqlServer);

        Assert.Equal("CREATE INDEX [IX_Order_Total] ON [Order] ([Total]);\n", sql);
    }

    [Fact]
    public void RenderCreateIndex_NoExplicitName_SynthesizesConventionalName()
    {
        var entity = Entity("Order", Column("Total", "decimal", isNullable: false));
        var index = new IndexModel(new[] { "Total" }, IsUnique: false);

        var sql = SqlDdlExporter.RenderCreateIndex(entity, index, ScaffoldProvider.SqlServer);

        Assert.Equal("CREATE INDEX [IX_Order_Total] ON [Order] ([Total]);\n", sql);
    }

    [Fact]
    public void RenderCreateIndex_Unique_EmitsUniqueKeyword()
    {
        var entity = Entity("User", Column("Email", "string", isNullable: false));
        var index = new IndexModel(new[] { "Email" }, IsUnique: true, Name: "IX_User_Email");

        var sql = SqlDdlExporter.RenderCreateIndex(entity, index, ScaffoldProvider.SqlServer);

        Assert.Equal("CREATE UNIQUE INDEX [IX_User_Email] ON [User] ([Email]);\n", sql);
    }

    [Fact]
    public void RenderCreateIndex_MultipleColumns_JoinsWithComma()
    {
        var entity = Entity("OrderLine",
            Column("OrderId", "int", isNullable: false), Column("LineNumber", "int", isNullable: false));
        var index = new IndexModel(new[] { "OrderId", "LineNumber" }, IsUnique: true, Name: "IX_OrderLine_Composite");

        var sql = SqlDdlExporter.RenderCreateIndex(entity, index, ScaffoldProvider.SqlServer);

        Assert.Equal("CREATE UNIQUE INDEX [IX_OrderLine_Composite] ON [OrderLine] ([OrderId], [LineNumber]);\n", sql);
    }

    [Fact]
    public void IsSkippedTphMember_RootEntity_IsFalse()
    {
        var person = Entity("Person") with { MappingStrategy = MappingStrategy.Tph };
        Assert.False(SqlDdlExporter.IsSkippedTphMember(person, new[] { person }));
    }

    [Fact]
    public void IsSkippedTphMember_DerivedTphEntity_IsTrue()
    {
        var person = Entity("Person") with { MappingStrategy = MappingStrategy.Tph };
        var student = Entity("Student") with { BaseEntityName = "Person", MappingStrategy = MappingStrategy.Tph };

        Assert.True(SqlDdlExporter.IsSkippedTphMember(student, new[] { person, student }));
    }

    [Fact]
    public void IsSkippedTphMember_DerivedTptEntity_IsFalse()
    {
        var person = Entity("Person") with { MappingStrategy = MappingStrategy.Tpt };
        var student = Entity("Student") with { BaseEntityName = "Person", MappingStrategy = MappingStrategy.Tpt };

        Assert.False(SqlDdlExporter.IsSkippedTphMember(student, new[] { person, student }));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter SqlDdlExporterTests`
Expected: FAIL (compile error — `RenderCreateIndex`/`IsSkippedTphMember` don't exist yet).

- [ ] **Step 3: Implement**

Add to `src/EfSchemaVisualizer.Web/Diagram/SqlDdlExporter.cs`:

```csharp
    internal static string RenderCreateIndex(EntityModel entity, IndexModel index, ScaffoldProvider provider)
    {
        var indexName = index.Name ?? $"IX_{PhysicalTableName(entity)}_{string.Join("_", index.PropertyNames)}";
        var uniqueKeyword = index.IsUnique ? "UNIQUE " : "";
        var columns = string.Join(", ", index.PropertyNames.Select(c => QuoteIdentifier(c, provider)));

        return $"CREATE {uniqueKeyword}INDEX {QuoteIdentifier(indexName, provider)} ON {QualifiedTableName(entity, provider)} ({columns});\n";
    }

    internal static bool IsSkippedTphMember(EntityModel entity, IReadOnlyList<EntityModel> allEntities) =>
        entity.MappingStrategy == MappingStrategy.Tph &&
        entity.BaseEntityName is not null &&
        allEntities.Any(e => e.Name == entity.BaseEntityName);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter SqlDdlExporterTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/SqlDdlExporter.cs tests/EfSchemaVisualizer.Web.Tests/Diagram/SqlDdlExporterTests.cs
git commit -m "Add SqlDdlExporter index rendering and TPH-member table-skip classification"
```

---

### Task 8: Foreign keys, sequences, and the public `Export` orchestrator

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/SqlDdlExporter.cs`
- Modify: `tests/EfSchemaVisualizer.Web.Tests/Diagram/SqlDdlExporterTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2–7.
- Produces: `public static string Export(DiagramModelResult result, ScaffoldProvider provider)` — the feature's public entry point, called from `Home.razor` in Task 9.

Foreign keys to emit, per the spec:
1. Every `RelationshipModel` with `Kind` in `{OneToOne, OneToMany}` and a non-empty `ForeignKeyProperties` — dependent table's `ForeignKeyProperties` reference the principal table's key (or `PrincipalKeyProperties` when set, for `HasPrincipalKey` cases).
2. Every `RelationshipModel` with `Kind == Inheritance` whose dependent entity's resolved `MappingStrategy == Tpt` — the child table's key columns are also a FK to the parent table's same-named key columns (the TPT PK-as-FK shape). `Kind == Inheritance` relationships under TPH/TPC are structural only and must NOT become a FK constraint (the TPH child has no table at all; TPC has no shared column).
3. Every `RelationshipModel` with `Kind == ManyToMany` — two FK constraints from the join entity (`JoinEntityName`) to each side, using `JoinEntityLeftForeignKey` → `PrincipalEntity`'s key and `JoinEntityRightForeignKey` → `DependentEntity`'s key.

The join entity's own `CREATE TABLE` (Task 5, called from this orchestrator) needs its primary key columns supplied explicitly as `JoinEntityLeftForeignKey.Concat(JoinEntityRightForeignKey)` when `entity.IsSharedType && entity.KeyPropertyNames.Count == 0` — that's exactly why Task 5's `RenderCreateTable` takes `primaryKeyColumnNames` as a caller-supplied parameter instead of always reading `entity.KeyPropertyNames`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/EfSchemaVisualizer.Web.Tests/Diagram/SqlDdlExporterTests.cs`:

```csharp
    [Fact]
    public void RenderSequence_EmitsCreateSequence()
    {
        var sequence = new SequenceModel("OrderNumbers", Schema: null, ClrType: "int", StartsAt: 1000, IncrementsBy: 1, MinValue: null, MaxValue: null, IsCyclic: null);

        var sql = SqlDdlExporter.RenderSequence(sequence, ScaffoldProvider.SqlServer);

        Assert.Equal("CREATE SEQUENCE [OrderNumbers] START WITH 1000 INCREMENT BY 1;\n", sql);
    }

    [Fact]
    public void Export_SimpleOneToMany_EmitsTablesThenForeignKey()
    {
        var blog = Entity("Blog", Column("Id", "int", isNullable: false)) with { KeyPropertyNames = new[] { "Id" } };
        var post = Entity("Post",
                Column("Id", "int", isNullable: false),
                Column("BlogId", "int", isNullable: false))
            with { KeyPropertyNames = new[] { "Id" } };
        var relationship = new RelationshipModel(
            "Blog", "Post", RelationshipKind.OneToMany, PrincipalNavigation: "Posts", DependentNavigation: "Blog",
            ForeignKeyProperties: new[] { "BlogId" }, ConstraintName: "FK_Post_Blog_BlogId");

        var result = new DiagramModelResult(new[] { blog, post }, new[] { relationship }, Array.Empty<Core.Parsing.Diagnostic>(), Array.Empty<SequenceModel>());

        var sql = SqlDdlExporter.Export(result, ScaffoldProvider.SqlServer);

        var blogIndex = sql.IndexOf("CREATE TABLE [Blog]", StringComparison.Ordinal);
        var postIndex = sql.IndexOf("CREATE TABLE [Post]", StringComparison.Ordinal);
        var fkIndex = sql.IndexOf("ALTER TABLE [Post] ADD CONSTRAINT [FK_Post_Blog_BlogId] FOREIGN KEY ([BlogId]) REFERENCES [Blog] ([Id]);", StringComparison.Ordinal);

        Assert.True(blogIndex >= 0 && postIndex > blogIndex, "Blog table must be created before Post table");
        Assert.True(fkIndex > postIndex, "Foreign key must be added after both tables exist");
    }

    [Fact]
    public void Export_ViewMappedEntity_ProducesNoCreateTable()
    {
        var view = Entity("OrderSummary", Column("Total", "decimal")) with { ViewName = "vw_OrderSummary" };
        var result = new DiagramModelResult(new[] { view }, Array.Empty<RelationshipModel>(), Array.Empty<Core.Parsing.Diagnostic>(), Array.Empty<SequenceModel>());

        var sql = SqlDdlExporter.Export(result, ScaffoldProvider.SqlServer);

        Assert.DoesNotContain("CREATE TABLE", sql);
    }

    [Fact]
    public void Export_TphHierarchy_EmitsOneTableForRootOnly()
    {
        var person = Entity("Person", Column("Id", "int", isNullable: false))
            with { KeyPropertyNames = new[] { "Id" }, MappingStrategy = MappingStrategy.Tph };
        var student = Entity("Student",
                new PropertyModel("Id", "int", false, null, DeclaringEntityName: "Person"),
                Column("Course", "string", isNullable: false))
            with { BaseEntityName = "Person", KeyPropertyNames = new[] { "Id" }, MappingStrategy = MappingStrategy.Tph };

        var result = new DiagramModelResult(new[] { person, student }, Array.Empty<RelationshipModel>(), Array.Empty<Core.Parsing.Diagnostic>(), Array.Empty<SequenceModel>());

        var sql = SqlDdlExporter.Export(result, ScaffoldProvider.SqlServer);

        Assert.Contains("CREATE TABLE [Person]", sql);
        Assert.DoesNotContain("CREATE TABLE [Student]", sql);
        Assert.Contains("[Course]", sql);
        Assert.Contains("[Discriminator]", sql);
    }

    [Fact]
    public void Export_TptHierarchy_EmitsOneTablePerEntityAndPkAsFk()
    {
        var person = Entity("Person", Column("Id", "int", isNullable: false))
            with { KeyPropertyNames = new[] { "Id" }, MappingStrategy = MappingStrategy.Tpt };
        var student = Entity("Student",
                new PropertyModel("Id", "int", false, null, DeclaringEntityName: "Person"),
                Column("Course", "string", isNullable: false))
            with { BaseEntityName = "Person", KeyPropertyNames = new[] { "Id" }, MappingStrategy = MappingStrategy.Tpt };
        var inheritance = new RelationshipModel("Person", "Student", RelationshipKind.Inheritance, null, null);

        var result = new DiagramModelResult(new[] { person, student }, new[] { inheritance }, Array.Empty<Core.Parsing.Diagnostic>(), Array.Empty<SequenceModel>());

        var sql = SqlDdlExporter.Export(result, ScaffoldProvider.SqlServer);

        Assert.Contains("CREATE TABLE [Person]", sql);
        Assert.Contains("CREATE TABLE [Student]", sql);
        Assert.Contains("ALTER TABLE [Student] ADD CONSTRAINT [FK_Student_Person] FOREIGN KEY ([Id]) REFERENCES [Person] ([Id]);", sql);
    }

    [Fact]
    public void Export_ManyToMany_EmitsJoinTableWithCompositeKeyAndTwoForeignKeys()
    {
        var post = Entity("Post", Column("Id", "int", isNullable: false)) with { KeyPropertyNames = new[] { "Id" } };
        var tag = Entity("Tag", Column("Id", "int", isNullable: false)) with { KeyPropertyNames = new[] { "Id" } };
        var join = Entity("PostTag",
                Column("PostsId", "int", isNullable: false),
                Column("TagsId", "int", isNullable: false))
            with { IsSharedType = true };
        var relationship = new RelationshipModel(
            "Post", "Tag", RelationshipKind.ManyToMany, PrincipalNavigation: "Tags", DependentNavigation: "Posts",
            JoinEntityName: "PostTag", JoinEntityIsSharedType: true,
            JoinEntityLeftForeignKey: new[] { "PostsId" }, JoinEntityRightForeignKey: new[] { "TagsId" });

        var result = new DiagramModelResult(new[] { post, tag, join }, new[] { relationship }, Array.Empty<Core.Parsing.Diagnostic>(), Array.Empty<SequenceModel>());

        var sql = SqlDdlExporter.Export(result, ScaffoldProvider.SqlServer);

        Assert.Contains("CONSTRAINT [PK_PostTag] PRIMARY KEY ([PostsId], [TagsId])", sql);
        Assert.Contains("ALTER TABLE [PostTag] ADD CONSTRAINT [FK_PostTag_Post] FOREIGN KEY ([PostsId]) REFERENCES [Post] ([Id]);", sql);
        Assert.Contains("ALTER TABLE [PostTag] ADD CONSTRAINT [FK_PostTag_Tag] FOREIGN KEY ([TagsId]) REFERENCES [Tag] ([Id]);", sql);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter SqlDdlExporterTests`
Expected: FAIL (compile error — `RenderSequence`/`Export` don't exist yet).

- [ ] **Step 3: Implement foreign keys, sequences, and `Export`**

Add to `src/EfSchemaVisualizer.Web/Diagram/SqlDdlExporter.cs`:

```csharp
    internal static string RenderSequence(SequenceModel sequence, ScaffoldProvider provider)
    {
        if (provider == ScaffoldProvider.Sqlite)
        {
            return $"-- SQLite has no CREATE SEQUENCE equivalent; skipped sequence \"{sequence.Name}\".\n";
        }

        var name = sequence.Schema is not null
            ? $"{QuoteIdentifier(sequence.Schema, provider)}.{QuoteIdentifier(sequence.Name, provider)}"
            : QuoteIdentifier(sequence.Name, provider);

        var clauses = new List<string>();
        if (sequence.StartsAt is long start)
        {
            clauses.Add($"START WITH {start}");
        }

        if (sequence.IncrementsBy is int increment)
        {
            clauses.Add($"INCREMENT BY {increment}");
        }

        var suffix = clauses.Count > 0 ? " " + string.Join(" ", clauses) : "";
        return $"CREATE SEQUENCE {name}{suffix};\n";
    }

    public static string Export(DiagramModelResult result, ScaffoldProvider provider)
    {
        var sb = new StringBuilder();

        foreach (var sequence in result.Sequences)
        {
            sb.Append(RenderSequence(sequence, provider));
        }

        var physicalEntities = SelectPhysicalEntities(result.Entities);
        var orderedEntities = OrderTablesByDependency(physicalEntities, result.Relationships);
        var byName = physicalEntities.ToDictionary(e => e.Name);

        foreach (var entity in orderedEntities)
        {
            if (IsSkippedTphMember(entity, physicalEntities))
            {
                continue;
            }

            var isTphRootWithDescendants =
                entity.MappingStrategy == MappingStrategy.Tph &&
                CollectDescendants(entity, physicalEntities).Count > 0;

            if (isTphRootWithDescendants)
            {
                var merged = BuildTphMergedEntity(entity, physicalEntities);
                sb.Append(RenderCreateTable(merged, merged.KeyPropertyNames, provider));
                continue;
            }

            var primaryKeyColumns = entity.IsSharedType && entity.KeyPropertyNames.Count == 0
                ? ResolveJoinEntityKeyColumns(entity, result.Relationships)
                : entity.KeyPropertyNames;

            sb.Append(RenderCreateTable(entity, primaryKeyColumns, provider));
        }

        foreach (var entity in orderedEntities)
        {
            if (IsSkippedTphMember(entity, physicalEntities))
            {
                continue;
            }

            foreach (var index in entity.Indexes)
            {
                sb.Append(RenderCreateIndex(entity, index, provider));
            }
        }

        foreach (var relationship in result.Relationships)
        {
            AppendForeignKey(sb, relationship, byName, provider);
        }

        return sb.ToString();
    }

    private static IReadOnlyList<string> ResolveJoinEntityKeyColumns(EntityModel joinEntity, IReadOnlyList<RelationshipModel> relationships)
    {
        var owning = relationships.FirstOrDefault(r => r.JoinEntityName == joinEntity.Name);
        return owning is null
            ? Array.Empty<string>()
            : owning.JoinEntityLeftForeignKey.Concat(owning.JoinEntityRightForeignKey).ToList();
    }

    private static void AppendForeignKey(
        StringBuilder sb, RelationshipModel relationship, Dictionary<string, EntityModel> byName, ScaffoldProvider provider)
    {
        switch (relationship.Kind)
        {
            case RelationshipKind.OneToOne or RelationshipKind.OneToMany when relationship.ForeignKeyProperties.Count > 0:
            {
                if (!byName.TryGetValue(relationship.DependentEntity, out var dependent) ||
                    !byName.TryGetValue(relationship.PrincipalEntity, out var principal))
                {
                    return;
                }

                var principalKeyColumns = relationship.PrincipalKeyProperties.Count > 0
                    ? relationship.PrincipalKeyProperties
                    : principal.KeyPropertyNames;

                var constraintName = relationship.ConstraintName
                    ?? $"FK_{PhysicalTableName(dependent)}_{PhysicalTableName(principal)}_{string.Join("_", relationship.ForeignKeyProperties)}";

                AppendAlterTableForeignKey(sb, dependent, principal, relationship.ForeignKeyProperties, principalKeyColumns, constraintName, provider);
                return;
            }

            case RelationshipKind.Inheritance:
            {
                if (!byName.TryGetValue(relationship.DependentEntity, out var child) ||
                    !byName.TryGetValue(relationship.PrincipalEntity, out var parent) ||
                    child.MappingStrategy != MappingStrategy.Tpt)
                {
                    return;
                }

                var keyColumns = child.KeyPropertyNames;
                var constraintName = $"FK_{PhysicalTableName(child)}_{PhysicalTableName(parent)}";
                AppendAlterTableForeignKey(sb, child, parent, keyColumns, keyColumns, constraintName, provider);
                return;
            }

            case RelationshipKind.ManyToMany when relationship.JoinEntityName is not null:
            {
                if (!byName.TryGetValue(relationship.JoinEntityName, out var join) ||
                    !byName.TryGetValue(relationship.PrincipalEntity, out var left) ||
                    !byName.TryGetValue(relationship.DependentEntity, out var right))
                {
                    return;
                }

                AppendAlterTableForeignKey(
                    sb, join, left, relationship.JoinEntityLeftForeignKey, left.KeyPropertyNames,
                    $"FK_{PhysicalTableName(join)}_{PhysicalTableName(left)}", provider);
                AppendAlterTableForeignKey(
                    sb, join, right, relationship.JoinEntityRightForeignKey, right.KeyPropertyNames,
                    $"FK_{PhysicalTableName(join)}_{PhysicalTableName(right)}", provider);
                return;
            }
        }
    }

    private static void AppendAlterTableForeignKey(
        StringBuilder sb, EntityModel dependent, EntityModel principal,
        IReadOnlyList<string> foreignKeyColumns, IReadOnlyList<string> principalKeyColumns,
        string constraintName, ScaffoldProvider provider)
    {
        var fkColumns = string.Join(", ", foreignKeyColumns.Select(c => QuoteIdentifier(c, provider)));
        var pkColumns = string.Join(", ", principalKeyColumns.Select(c => QuoteIdentifier(c, provider)));

        sb.Append("ALTER TABLE ").Append(QualifiedTableName(dependent, provider))
          .Append(" ADD CONSTRAINT ").Append(QuoteIdentifier(constraintName, provider))
          .Append(" FOREIGN KEY (").Append(fkColumns).Append(") REFERENCES ")
          .Append(QualifiedTableName(principal, provider)).Append(" (").Append(pkColumns).Append(");\n");
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter SqlDdlExporterTests`
Expected: PASS. Then run the full suite: `dotnet test`. Expected: all green (no regressions in Core or other Web tests).

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/SqlDdlExporter.cs tests/EfSchemaVisualizer.Web.Tests/Diagram/SqlDdlExporterTests.cs
git commit -m "Add SqlDdlExporter foreign keys, sequences, and the public Export orchestrator"
```

---

### Task 9: Wire "Export SQL" into `Home.razor`

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Pages/Home.razor`

**Interfaces:**
- Consumes: `SqlDdlExporter.Export(DiagramModelResult, ScaffoldProvider)` (Task 8), existing `_editor`/`_selectedProvider`/`DownloadTextAsync` fields and helpers already present in `Home.razor` (see `ExportMermaidAsync` at line 466 and the `_selectedProvider` field used by the scaffold checkbox).

This task has no automated test (it's a thin UI wiring change over an already-tested pure function) — verification is a manual smoke check in Step 3.

- [ ] **Step 1: Add the button next to Export Mermaid**

In `src/EfSchemaVisualizer.Web/Pages/Home.razor`, find this line (around line 72):

```razor
        <button class="btn btn-secondary" title="Export the diagram as Mermaid erDiagram text" disabled="@(_editor is null)" @onclick="ExportMermaidAsync">Export Mermaid</button>
```

Add immediately after it:

```razor
        <select title="SQL dialect for Export SQL" @bind="_selectedProvider">
            <option value="@ScaffoldProvider.SqlServer">SQL Server</option>
            <option value="@ScaffoldProvider.PostgreSql">PostgreSQL</option>
            <option value="@ScaffoldProvider.Sqlite">SQLite</option>
        </select>
        <button class="btn btn-secondary" title="Export the diagram as CREATE TABLE / ALTER TABLE SQL DDL" disabled="@(_editor is null)" @onclick="ExportSqlAsync">Export SQL</button>
```

This reuses the existing `_selectedProvider` field (already declared for the scaffold checkbox) rather than adding a second dropdown/field — so choosing a dialect here also affects the scaffold checkbox's provider choice and vice versa, which is the intended behavior per the design (one dialect concept, two consumers).

- [ ] **Step 2: Add `ExportSqlAsync` next to `ExportMermaidAsync`**

Find `ExportMermaidAsync` in the `@code` block (around line 466):

```csharp
    private async Task ExportMermaidAsync()
    {
        if (_editor is null)
        {
            return;
        }

        var mermaid = MermaidExporter.Export(_editor.Current);
        await DownloadTextAsync("diagram.mmd", mermaid, "text/plain");
    }
```

Add immediately after it:

```csharp
    private async Task ExportSqlAsync()
    {
        if (_editor is null)
        {
            return;
        }

        var sql = SqlDdlExporter.Export(_editor.Current, _selectedProvider);
        await DownloadTextAsync("schema.sql", sql, "text/plain");
    }
```

- [ ] **Step 3: Build and manually verify in the browser**

Run: `dotnet build` — expect success with no new warnings/errors.

Then start the dev server (check for a project-specific run skill/script first; otherwise `dotnet run --project src/EfSchemaVisualizer.Web`) and in the browser:
1. Load the shipped sample (or paste any entity classes + config).
2. Click "Render Diagram".
3. Change the new dialect dropdown to each of SQL Server / PostgreSQL / SQLite in turn and click "Export SQL" each time.
4. Confirm a `schema.sql` file downloads each time and its contents contain `CREATE TABLE` statements with the dialect-appropriate quoting (`[...]` for SQL Server, `"..."` for the other two).
5. Confirm the existing "Export Mermaid" and "Generate runnable project scaffold" controls still work unaffected (the shared `_selectedProvider` field didn't break the scaffold flow).

- [ ] **Step 4: Run the full test suite one more time**

Run: `dotnet test`
Expected: all green, same pass count as Task 8 plus no new failures.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Pages/Home.razor
git commit -m "Wire Export SQL button into Home.razor download toolbar"
```

---

## Self-Review Notes

- **Spec coverage:** sequences (Task 8), plain tables with PK/CHECK/alternate-key (Task 5), TPH merged table + discriminator (Task 6), TPT PK-as-FK (Task 8's `AppendForeignKey` `Inheritance` case), TPC (no special casing needed — folded columns already flat per existing `InheritanceInference.Fold`, rendered via the same plain-table path as Task 5, confirmed no separate task needed since TPC entities need zero extra logic beyond what Task 5/8 already do), many-to-many join tables (Task 8), indexes (Task 7), view/function-mapped skip (Task 4/8), identifier quoting and schema qualification (Task 2) are all covered. Owned-standalone (`OwnsMany`) entities fall through to the plain-table path with no special handling, matching the spec's documented non-goal (no owner-FK synthesis) with zero extra code required.
- **Placeholder scan:** no TBD/TODO; every step has runnable code and literal expected test strings.
- **Type consistency:** `SqlDdlExporter.RenderCreateTable`'s `primaryKeyColumnNames` parameter name and `SqlColumnTypeMapper.MapType`'s signature are used identically across Tasks 5–8.

## Execution Options

Plan complete and saved to `docs/superpowers/plans/2026-07-31-sql-ddl-export.md`. Two execution options:

1. **Subagent-Driven (recommended)** - I dispatch a fresh subagent per task, review between tasks, fast iteration
2. **Inline Execution** - Execute tasks in this session using executing-plans, batch execution with checkpoints

Which approach?
