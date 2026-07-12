# Column / Table Mapping Fluent Config Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add full round-trip support (model, parse, merge, rewrite) for EF Core's `ToTable(...)` (entity-level table/schema mapping), `HasColumnName(...)`, `HasColumnType(...)`, and `HasDefaultValue(...)` (all per-property) fluent config calls.

**Architecture:** Four independent fluent-call kinds, each following the existing `HasMaxLength`/`HasKey` parse → merge → rewrite pattern, each with its own config DTO (consistent with `MaxLengthConfig`/`KeyConfig`/`IndexConfig` being separate types). `EntityModel` gains `TableName`/`Schema` (entity-level, like `KeyPropertyNames`). `PropertyModel` gains `ColumnName`/`ColumnType`/`DefaultValueLiteral` (per-property, like `MaxLength`). Table mapping reuses the three-case dispatch built for `HasKey` (mutate/insert-statement/synthesize-block, no bare-receiver append). The three per-property mappings reuse the four-case dispatch built for `HasMaxLength` (mutate/append/insert-statement/synthesize-block).

**Tech Stack:** C# / .NET 10, Roslyn (`Microsoft.CodeAnalysis.CSharp`), xUnit.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-07-12-column-table-mapping-config-design.md` — read it before starting; this plan implements it verbatim.
- `TargetFramework` is `net10.0`, `Nullable` is enabled, on both `src/EfSchemaVisualizer.Core` and `tests/EfSchemaVisualizer.Core.Tests` — match existing nullable-annotation style in the files you touch.
- Never silently drop config the parser can't read — unreadable shapes must emit a `Diagnostic` (Priority 0 project rule; see spec's diagnostic codes).
- New public methods are separate composed calls, not orchestrated into existing ones — each `Apply*`/`Set*`/`Remove*` is called independently by whatever composes them.
- Run tests with `dotnet test --filter "FullyQualifiedName~<TestClassName>"` from the repo root (`/root/RiderProjects/EfSchemaVisualizer`); this project has no solution-wide script beyond `dotnet test`.
- Commit after each task, not each step.
- This plan builds on the `HasPrecision` plan's pattern but has no code dependency on it — the two can be implemented in either order.

---

### Task 1: `EntityModel.TableName`/`Schema`, `PropertyModel` column fields, and config DTOs

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`
- Modify: `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`
- Create: `src/EfSchemaVisualizer.Core/Parsing/TableConfig.cs`
- Create: `src/EfSchemaVisualizer.Core/Parsing/ColumnNameConfig.cs`
- Create: `src/EfSchemaVisualizer.Core/Parsing/ColumnTypeConfig.cs`
- Create: `src/EfSchemaVisualizer.Core/Parsing/DefaultValueConfig.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs`

**Interfaces:**
- Produces: `EntityModel.TableName` (`string?`), `EntityModel.Schema` (`string?`), both default `null`.
- Produces: `PropertyModel.ColumnName`/`ColumnType`/`DefaultValueLiteral` (all `string?`), default `null`.
- Produces: `TableConfig(string EntityName, string TableName, string? Schema)`, `ColumnNameConfig(string EntityName, string PropertyName, string ColumnName)`, `ColumnTypeConfig(string EntityName, string PropertyName, string ColumnType)`, `DefaultValueConfig(string EntityName, string PropertyName, string LiteralText)`.

- [ ] **Step 1: Write the failing test**

Add to `tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs`, inside the `PropertyModelTests` class, after `EntityModel_WithIndexes_ProducesUpdatedCopy_LeavingOriginalUnchanged`:

```csharp
    [Fact]
    public void EntityModel_TableNameAndSchema_DefaultToNull()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>());

        Assert.Null(entity.TableName);
        Assert.Null(entity.Schema);
    }

    [Fact]
    public void EntityModel_WithTableNameAndSchema_ProducesUpdatedCopy_LeavingOriginalUnchanged()
    {
        var original = new EntityModel("Person", new List<PropertyModel>());

        var updated = original with { TableName = "People", Schema = "dbo" };

        Assert.Null(original.TableName);
        Assert.Equal("People", updated.TableName);
        Assert.Equal("dbo", updated.Schema);
    }
```

And after `Precision_And_Scale_DefaultToNull` (or, if the `HasPrecision` plan hasn't been implemented yet, after `WithIsRequiredOverride_ProducesUpdatedCopy_LeavingOriginalUnchanged`):

```csharp
    [Fact]
    public void WithColumnMapping_ProducesUpdatedCopy_LeavingOriginalUnchanged()
    {
        var original = new PropertyModel("Total", "decimal", IsNullable: false, MaxLength: null);

        var updated = original with { ColumnName = "total_amount", ColumnType = "decimal(18,2)", DefaultValueLiteral = "0" };

        Assert.Null(original.ColumnName);
        Assert.Null(original.ColumnType);
        Assert.Null(original.DefaultValueLiteral);
        Assert.Equal("total_amount", updated.ColumnName);
        Assert.Equal("decimal(18,2)", updated.ColumnType);
        Assert.Equal("0", updated.DefaultValueLiteral);
    }

    [Fact]
    public void ColumnMappingFields_DefaultToNull()
    {
        var property = new PropertyModel("Total", "decimal", IsNullable: false, MaxLength: null);

        Assert.Null(property.ColumnName);
        Assert.Null(property.ColumnType);
        Assert.Null(property.DefaultValueLiteral);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~PropertyModelTests"`
Expected: build error — `EntityModel` has no members `TableName`/`Schema`; `PropertyModel` has no members `ColumnName`/`ColumnType`/`DefaultValueLiteral`.

- [ ] **Step 3: Implement**

Read the current file first (`Read src/EfSchemaVisualizer.Core/Model/EntityModel.cs`). If the `HasPrecision` plan has already added `PropertyModel.Precision`/`Scale`, `EntityModel` itself is untouched by that plan — only add the two new fields here. Replace `EntityModel.cs` contents with:

```csharp
using System.Collections.Generic;

namespace EfSchemaVisualizer.Core.Model;

public sealed record EntityModel(
    string Name,
    IReadOnlyList<PropertyModel> Properties,
    IReadOnlyList<string>? KeyPropertyNames = null,
    IReadOnlyList<IndexModel>? Indexes = null,
    string? TableName = null,
    string? Schema = null)
{
    public IReadOnlyList<string> KeyPropertyNames { get; init; } = KeyPropertyNames ?? new List<string>();
    public IReadOnlyList<IndexModel> Indexes { get; init; } = Indexes ?? new List<IndexModel>();
}
```

Read the current file first (`Read src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`). If the `HasPrecision` plan has already run, the file already has `Precision`/`Scale` fields — add the three new fields after them. The full file (assuming `HasPrecision` already applied) becomes:

```csharp
namespace EfSchemaVisualizer.Core.Model;

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

If `HasPrecision` has *not* been applied yet, omit the `Precision`/`Scale` parameters and append the three new ones directly after `IsRequiredOverride = null`.

Create `src/EfSchemaVisualizer.Core/Parsing/TableConfig.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Parsing;

public sealed record TableConfig(string EntityName, string TableName, string? Schema);
```

Create `src/EfSchemaVisualizer.Core/Parsing/ColumnNameConfig.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Parsing;

public sealed record ColumnNameConfig(string EntityName, string PropertyName, string ColumnName);
```

Create `src/EfSchemaVisualizer.Core/Parsing/ColumnTypeConfig.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Parsing;

public sealed record ColumnTypeConfig(string EntityName, string PropertyName, string ColumnType);
```

Create `src/EfSchemaVisualizer.Core/Parsing/DefaultValueConfig.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Parsing;

public sealed record DefaultValueConfig(string EntityName, string PropertyName, string LiteralText);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~PropertyModelTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Model/EntityModel.cs src/EfSchemaVisualizer.Core/Model/PropertyModel.cs src/EfSchemaVisualizer.Core/Parsing/TableConfig.cs src/EfSchemaVisualizer.Core/Parsing/ColumnNameConfig.cs src/EfSchemaVisualizer.Core/Parsing/ColumnTypeConfig.cs src/EfSchemaVisualizer.Core/Parsing/DefaultValueConfig.cs tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs
git commit -m "Add table/column mapping fields to EntityModel/PropertyModel and config DTOs"
```

---

### Task 2: `FluentConfigParser.ParseTableMappings`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.GetConfiguredEntityName`, `FindEntityConfigInvocations`, `FindCallsNamed` (existing, unchanged).
- Produces: `FluentConfigParser.ParseTableMappings(string sourceCode) : ParseResult<IReadOnlyList<TableConfig>>`. New diagnostic code `UnreadableToTableArgument` (entity populated, `PropertyName: null`, same convention as `UnreadableHasKeyArgument`).

- [ ] **Step 1: Write the failing test**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`, inside the `FluentConfigParserTests` class (near the end):

```csharp
    private const string TableMappingSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.ToTable("People", "dbo");
                });

                modelBuilder.Entity<Address>(entity =>
                {
                    entity.ToTable("Addresses");
                });
            }
        }
        """;

    [Fact]
    public void ParseTableMappings_ReadsTableNameOnly_AndTableNameWithSchema()
    {
        var result = new FluentConfigParser().ParseTableMappings(TableMappingSource);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Value.Count);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", TableName: "People", Schema: "dbo" });
        Assert.Contains(result.Value, c => c is { EntityName: "Address", TableName: "Addresses", Schema: null });
    }

    private const string TableMappingSourceWithNonLiteralArg = """
        public class AppDbContext : DbContext
        {
            private const string TableName = "People";

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.ToTable(TableName);
                });
            }
        }
        """;

    [Fact]
    public void ParseTableMappings_NonLiteralArgument_EmitsUnreadableToTableArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParseTableMappings(TableMappingSourceWithNonLiteralArg);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("UnreadableToTableArgument", diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
        Assert.Null(diagnostic.PropertyName);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~FluentConfigParserTests"`
Expected: build error — `FluentConfigParser` has no method `ParseTableMappings`.

- [ ] **Step 3: Implement**

Add this method to the `FluentConfigParser` class (place it after `ParseKeys` and its private helpers):

```csharp
    public ParseResult<IReadOnlyList<TableConfig>> ParseTableMappings(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<TableConfig>();
        var diagnostics = new List<Diagnostic>();

        var entityNames = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(FluentSyntaxHelpers.GetConfiguredEntityName)
            .Where(name => name is not null)
            .Distinct()!;

        foreach (var entityName in entityNames)
        {
            foreach (var entityInvocation in FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName!))
            {
                foreach (var toTableCall in FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "ToTable"))
                {
                    var arguments = toTableCall.ArgumentList.Arguments;

                    if (arguments.Count == 0
                        || arguments[0].Expression is not LiteralExpressionSyntax { } tableNameLiteral
                        || !tableNameLiteral.IsKind(SyntaxKind.StringLiteralExpression))
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnreadableToTableArgument",
                            "ToTable argument is not a string literal and could not be read.",
                            entityName,
                            PropertyName: null,
                            toTableCall.Span));
                        continue;
                    }

                    string? schema = null;
                    if (arguments.Count >= 2)
                    {
                        if (arguments[1].Expression is LiteralExpressionSyntax { } schemaLiteral
                            && schemaLiteral.IsKind(SyntaxKind.StringLiteralExpression))
                        {
                            schema = schemaLiteral.Token.ValueText;
                        }
                        else
                        {
                            diagnostics.Add(new Diagnostic(
                                "UnreadableToTableArgument",
                                "ToTable schema argument is not a string literal and could not be read.",
                                entityName,
                                PropertyName: null,
                                toTableCall.Span));
                            continue;
                        }
                    }

                    results.Add(new TableConfig(entityName!, tableNameLiteral.Token.ValueText, schema));
                }
            }
        }

        return new ParseResult<IReadOnlyList<TableConfig>>(results, diagnostics);
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~FluentConfigParserTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Add FluentConfigParser.ParseTableMappings"
```

---

### Task 3: `FluentConfigParser.ParseColumnNames`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`

**Interfaces:**
- Produces: `FluentConfigParser.ParseColumnNames(string sourceCode) : ParseResult<IReadOnlyList<ColumnNameConfig>>`. New diagnostic code `UnreadableHasColumnNameArgument` (entity + property populated).

- [ ] **Step 1: Write the failing test**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`:

```csharp
    private const string ColumnNameSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasColumnName("full_name");
                });
            }
        }
        """;

    [Fact]
    public void ParseColumnNames_ReadsStringLiteralArgument()
    {
        var result = new FluentConfigParser().ParseColumnNames(ColumnNameSource);

        Assert.Empty(result.Diagnostics);
        var config = Assert.Single(result.Value);
        Assert.Equal("Person", config.EntityName);
        Assert.Equal("Name", config.PropertyName);
        Assert.Equal("full_name", config.ColumnName);
    }

    private const string ColumnNameSourceWithNonLiteralArg = """
        public class AppDbContext : DbContext
        {
            private const string ColumnName = "full_name";

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasColumnName(ColumnName);
                });
            }
        }
        """;

    [Fact]
    public void ParseColumnNames_NonLiteralArgument_EmitsUnreadableHasColumnNameArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParseColumnNames(ColumnNameSourceWithNonLiteralArg);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("UnreadableHasColumnNameArgument", diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
        Assert.Equal("Name", diagnostic.PropertyName);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~FluentConfigParserTests"`
Expected: build error — `FluentConfigParser` has no method `ParseColumnNames`.

- [ ] **Step 3: Implement**

Add this method to the `FluentConfigParser` class (place it after `ParseTableMappings`):

```csharp
    public ParseResult<IReadOnlyList<ColumnNameConfig>> ParseColumnNames(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<ColumnNameConfig>();
        var diagnostics = new List<Diagnostic>();

        var entityNames = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(FluentSyntaxHelpers.GetConfiguredEntityName)
            .Where(name => name is not null)
            .Distinct()!;

        foreach (var entityName in entityNames)
        {
            foreach (var entityInvocation in FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName!))
            {
                foreach (var call in FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasColumnName"))
                {
                    var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(call);

                    if (propertyName is null)
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnresolvablePropertyName",
                            "Could not determine which property this HasColumnName call configures.",
                            entityName,
                            PropertyName: null,
                            call.Span));
                        continue;
                    }

                    var arg = call.ArgumentList.Arguments.FirstOrDefault();

                    if (arg?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                    {
                        results.Add(new ColumnNameConfig(entityName!, propertyName, literal.Token.ValueText));
                    }
                    else
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnreadableHasColumnNameArgument",
                            "HasColumnName argument is not a string literal and could not be read.",
                            entityName,
                            propertyName,
                            (arg ?? (SyntaxNode)call).Span));
                    }
                }
            }
        }

        return new ParseResult<IReadOnlyList<ColumnNameConfig>>(results, diagnostics);
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~FluentConfigParserTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Add FluentConfigParser.ParseColumnNames"
```

---

### Task 4: `FluentConfigParser.ParseColumnTypes`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`

**Interfaces:**
- Produces: `FluentConfigParser.ParseColumnTypes(string sourceCode) : ParseResult<IReadOnlyList<ColumnTypeConfig>>`. New diagnostic code `UnreadableHasColumnTypeArgument` (entity + property populated).

- [ ] **Step 1: Write the failing test**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`:

```csharp
    private const string ColumnTypeSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.Property(e => e.Total).HasColumnType("decimal(18,2)");
                });
            }
        }
        """;

    [Fact]
    public void ParseColumnTypes_ReadsStringLiteralArgument()
    {
        var result = new FluentConfigParser().ParseColumnTypes(ColumnTypeSource);

        Assert.Empty(result.Diagnostics);
        var config = Assert.Single(result.Value);
        Assert.Equal("Order", config.EntityName);
        Assert.Equal("Total", config.PropertyName);
        Assert.Equal("decimal(18,2)", config.ColumnType);
    }

    private const string ColumnTypeSourceWithNonLiteralArg = """
        public class AppDbContext : DbContext
        {
            private const string ColumnType = "decimal(18,2)";

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.Property(e => e.Total).HasColumnType(ColumnType);
                });
            }
        }
        """;

    [Fact]
    public void ParseColumnTypes_NonLiteralArgument_EmitsUnreadableHasColumnTypeArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParseColumnTypes(ColumnTypeSourceWithNonLiteralArg);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("UnreadableHasColumnTypeArgument", diagnostic.Code);
        Assert.Equal("Order", diagnostic.EntityName);
        Assert.Equal("Total", diagnostic.PropertyName);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~FluentConfigParserTests"`
Expected: build error — `FluentConfigParser` has no method `ParseColumnTypes`.

- [ ] **Step 3: Implement**

Add this method to the `FluentConfigParser` class (place it after `ParseColumnNames`):

```csharp
    public ParseResult<IReadOnlyList<ColumnTypeConfig>> ParseColumnTypes(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<ColumnTypeConfig>();
        var diagnostics = new List<Diagnostic>();

        var entityNames = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(FluentSyntaxHelpers.GetConfiguredEntityName)
            .Where(name => name is not null)
            .Distinct()!;

        foreach (var entityName in entityNames)
        {
            foreach (var entityInvocation in FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName!))
            {
                foreach (var call in FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasColumnType"))
                {
                    var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(call);

                    if (propertyName is null)
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnresolvablePropertyName",
                            "Could not determine which property this HasColumnType call configures.",
                            entityName,
                            PropertyName: null,
                            call.Span));
                        continue;
                    }

                    var arg = call.ArgumentList.Arguments.FirstOrDefault();

                    if (arg?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                    {
                        results.Add(new ColumnTypeConfig(entityName!, propertyName, literal.Token.ValueText));
                    }
                    else
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnreadableHasColumnTypeArgument",
                            "HasColumnType argument is not a string literal and could not be read.",
                            entityName,
                            propertyName,
                            (arg ?? (SyntaxNode)call).Span));
                    }
                }
            }
        }

        return new ParseResult<IReadOnlyList<ColumnTypeConfig>>(results, diagnostics);
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~FluentConfigParserTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Add FluentConfigParser.ParseColumnTypes"
```

---

### Task 5: `FluentConfigParser.ParseDefaultValues`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`

**Interfaces:**
- Produces: `FluentConfigParser.ParseDefaultValues(string sourceCode) : ParseResult<IReadOnlyList<DefaultValueConfig>>`. New diagnostic code `UnreadableHasDefaultValueArgument` (entity + property populated).

- [ ] **Step 1: Write the failing test**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`:

```csharp
    private const string DefaultValueSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.Property(e => e.Quantity).HasDefaultValue(1);
                    entity.Property(e => e.Status).HasDefaultValue("pending");
                    entity.Property(e => e.IsArchived).HasDefaultValue(false);
                    entity.Property(e => e.CanceledAt).HasDefaultValue(null);
                });
            }
        }
        """;

    [Fact]
    public void ParseDefaultValues_ReadsNumericStringBoolAndNullLiterals()
    {
        var result = new FluentConfigParser().ParseDefaultValues(DefaultValueSource);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(4, result.Value.Count);
        Assert.Contains(result.Value, c => c is { EntityName: "Order", PropertyName: "Quantity", LiteralText: "1" });
        Assert.Contains(result.Value, c => c is { EntityName: "Order", PropertyName: "Status", LiteralText: "\"pending\"" });
        Assert.Contains(result.Value, c => c is { EntityName: "Order", PropertyName: "IsArchived", LiteralText: "false" });
        Assert.Contains(result.Value, c => c is { EntityName: "Order", PropertyName: "CanceledAt", LiteralText: "null" });
    }

    private const string DefaultValueSourceWithMemberAccessArg = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.Property(e => e.CreatedAt).HasDefaultValue(DateTime.UtcNow);
                });
            }
        }
        """;

    [Fact]
    public void ParseDefaultValues_MemberAccessArgument_EmitsUnreadableHasDefaultValueArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParseDefaultValues(DefaultValueSourceWithMemberAccessArg);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("UnreadableHasDefaultValueArgument", diagnostic.Code);
        Assert.Equal("Order", diagnostic.EntityName);
        Assert.Equal("CreatedAt", diagnostic.PropertyName);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~FluentConfigParserTests"`
Expected: build error — `FluentConfigParser` has no method `ParseDefaultValues`.

- [ ] **Step 3: Implement**

Add this method to the `FluentConfigParser` class (place it after `ParseColumnTypes`):

```csharp
    public ParseResult<IReadOnlyList<DefaultValueConfig>> ParseDefaultValues(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<DefaultValueConfig>();
        var diagnostics = new List<Diagnostic>();

        var entityNames = root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Select(FluentSyntaxHelpers.GetConfiguredEntityName)
            .Where(name => name is not null)
            .Distinct()!;

        foreach (var entityName in entityNames)
        {
            foreach (var entityInvocation in FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName!))
            {
                foreach (var call in FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasDefaultValue"))
                {
                    var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(call);

                    if (propertyName is null)
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnresolvablePropertyName",
                            "Could not determine which property this HasDefaultValue call configures.",
                            entityName,
                            PropertyName: null,
                            call.Span));
                        continue;
                    }

                    var arg = call.ArgumentList.Arguments.FirstOrDefault();

                    if (arg?.Expression is LiteralExpressionSyntax literal)
                    {
                        results.Add(new DefaultValueConfig(entityName!, propertyName, literal.ToString()));
                    }
                    else
                    {
                        diagnostics.Add(new Diagnostic(
                            "UnreadableHasDefaultValueArgument",
                            "HasDefaultValue argument is not a literal and could not be read.",
                            entityName,
                            propertyName,
                            (arg ?? (SyntaxNode)call).Span));
                    }
                }
            }
        }

        return new ParseResult<IReadOnlyList<DefaultValueConfig>>(results, diagnostics);
    }
```

Note: `literal.ToString()` (not `.Token.ValueText`) is used deliberately here, unlike `ColumnName`/`ColumnType` — this preserves the literal's exact source text (e.g. `"pending"` with quotes, `null`, `false`) rather than the string's unescaped value, per the spec's "store exact source text" design for `DefaultValueLiteral`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~FluentConfigParserTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS, all tests across the whole solution green.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Add FluentConfigParser.ParseDefaultValues"
```

---

### Task 6: `ModelMerger.ApplyTableMapping`, `ApplyColumnNames`, `ApplyColumnTypes`, `ApplyDefaultValues`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/ModelMerger.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/ModelMergerTests.cs`

**Interfaces:**
- Consumes: `TableConfig`, `ColumnNameConfig`, `ColumnTypeConfig`, `DefaultValueConfig` (Task 1); `EntityModel.TableName`/`Schema`, `PropertyModel.ColumnName`/`ColumnType`/`DefaultValueLiteral` (Task 1).
- Produces: `ModelMerger.ApplyTableMapping(EntityModel, IReadOnlyList<TableConfig>) : EntityModel`, `ApplyColumnNames(EntityModel, IReadOnlyList<ColumnNameConfig>) : EntityModel`, `ApplyColumnTypes(EntityModel, IReadOnlyList<ColumnTypeConfig>) : EntityModel`, `ApplyDefaultValues(EntityModel, IReadOnlyList<DefaultValueConfig>) : EntityModel`.

- [ ] **Step 1: Write the failing test**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/ModelMergerTests.cs`, inside the `ModelMergerTests` class:

```csharp
    [Fact]
    public void ApplyTableMapping_SetsTableNameAndSchema_OnMatchingEntity()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>());

        var configs = new List<TableConfig>
        {
            new("Person", "People", "dbo"),
            new("Address", "Addresses", null), // different entity, must not affect Person
        };

        var merged = ModelMerger.ApplyTableMapping(entity, configs);

        Assert.Equal("People", merged.TableName);
        Assert.Equal("dbo", merged.Schema);
    }

    [Fact]
    public void ApplyTableMapping_NoMatchingConfig_LeavesTableNameAndSchemaNull()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>());

        var merged = ModelMerger.ApplyTableMapping(entity, new List<TableConfig>());

        Assert.Null(merged.TableName);
        Assert.Null(merged.Schema);
    }

    [Fact]
    public void ApplyColumnNames_SetsColumnNameOnMatchingProperty_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Name", "string", IsNullable: true, MaxLength: null),
        });

        var configs = new List<ColumnNameConfig>
        {
            new("Person", "Name", "full_name"),
            new("Address", "Line1", "line_1"), // different entity, must not affect Person
        };

        var merged = ModelMerger.ApplyColumnNames(entity, configs);

        Assert.Null(merged.Properties.Single(p => p.Name == "Id").ColumnName);
        Assert.Equal("full_name", merged.Properties.Single(p => p.Name == "Name").ColumnName);
    }

    [Fact]
    public void ApplyColumnTypes_SetsColumnTypeOnMatchingProperty_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Order", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Total", "decimal", IsNullable: false, MaxLength: null),
        });

        var configs = new List<ColumnTypeConfig>
        {
            new("Order", "Total", "decimal(18,2)"),
        };

        var merged = ModelMerger.ApplyColumnTypes(entity, configs);

        Assert.Null(merged.Properties.Single(p => p.Name == "Id").ColumnType);
        Assert.Equal("decimal(18,2)", merged.Properties.Single(p => p.Name == "Total").ColumnType);
    }

    [Fact]
    public void ApplyDefaultValues_SetsDefaultValueLiteralOnMatchingProperty_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Order", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Quantity", "int", IsNullable: false, MaxLength: null),
        });

        var configs = new List<DefaultValueConfig>
        {
            new("Order", "Quantity", "1"),
        };

        var merged = ModelMerger.ApplyDefaultValues(entity, configs);

        Assert.Null(merged.Properties.Single(p => p.Name == "Id").DefaultValueLiteral);
        Assert.Equal("1", merged.Properties.Single(p => p.Name == "Quantity").DefaultValueLiteral);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ModelMergerTests"`
Expected: build error — `ModelMerger` has no methods `ApplyTableMapping`/`ApplyColumnNames`/`ApplyColumnTypes`/`ApplyDefaultValues`.

- [ ] **Step 3: Implement**

Add these methods to the `ModelMerger` class (place them after `ApplyIndexes`):

```csharp
    public static EntityModel ApplyTableMapping(EntityModel entity, IReadOnlyList<TableConfig> configs)
    {
        var config = configs.FirstOrDefault(c => c.EntityName == entity.Name);

        return config is null ? entity : entity with { TableName = config.TableName, Schema = config.Schema };
    }

    public static EntityModel ApplyColumnNames(EntityModel entity, IReadOnlyList<ColumnNameConfig> configs)
    {
        var updatedProperties = entity.Properties
            .Select(property =>
            {
                var config = configs.FirstOrDefault(c =>
                    c.EntityName == entity.Name && c.PropertyName == property.Name);

                return config is null ? property : property with { ColumnName = config.ColumnName };
            })
            .ToList();

        return entity with { Properties = updatedProperties };
    }

    public static EntityModel ApplyColumnTypes(EntityModel entity, IReadOnlyList<ColumnTypeConfig> configs)
    {
        var updatedProperties = entity.Properties
            .Select(property =>
            {
                var config = configs.FirstOrDefault(c =>
                    c.EntityName == entity.Name && c.PropertyName == property.Name);

                return config is null ? property : property with { ColumnType = config.ColumnType };
            })
            .ToList();

        return entity with { Properties = updatedProperties };
    }

    public static EntityModel ApplyDefaultValues(EntityModel entity, IReadOnlyList<DefaultValueConfig> configs)
    {
        var updatedProperties = entity.Properties
            .Select(property =>
            {
                var config = configs.FirstOrDefault(c =>
                    c.EntityName == entity.Name && c.PropertyName == property.Name);

                return config is null ? property : property with { DefaultValueLiteral = config.LiteralText };
            })
            .ToList();

        return entity with { Properties = updatedProperties };
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~ModelMergerTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/ModelMerger.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/ModelMergerTests.cs
git commit -m "Add ModelMerger table/column mapping Apply* methods"
```

---

### Task 7: `OnModelCreatingRewriter.SetTable`/`RemoveTable`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.FindEntityConfigInvocations`, `FindCallsNamed` (existing); `OnModelCreatingRewriter.FindOnModelCreatingMethod`, `BuildEntityInvocationStatement` (existing private helpers).
- Produces: `OnModelCreatingRewriter.SetTable(string sourceCode, string entityName, string tableName, string? schema) : string`, `RemoveTable(string sourceCode, string entityName) : string`.

- [ ] **Step 1: Write the failing test**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`, inside the `OnModelCreatingRewriterTests` class:

```csharp
    private const string TableMappingSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.ToTable("People", "dbo");
                });
            }
        }
        """;

    [Fact]
    public void SetTable_ExistingCall_MutatesArguments()
    {
        var result = new OnModelCreatingRewriter()
            .SetTable(TableMappingSource, entityName: "Person", tableName: "Persons", schema: "sales");

        Assert.Contains("entity.ToTable(\"Persons\", \"sales\")", result);
        Assert.DoesNotContain("ToTable(\"People\", \"dbo\")", result);
    }

    [Fact]
    public void SetTable_ExistingCall_MutatesFromSchemaToNoSchema()
    {
        var result = new OnModelCreatingRewriter()
            .SetTable(TableMappingSource, entityName: "Person", tableName: "Persons", schema: null);

        Assert.Contains("entity.ToTable(\"Persons\")", result);
        Assert.DoesNotContain("ToTable(\"People\", \"dbo\")", result);
    }

    private const string SourceWithEntityConfiguredNoTable = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasMaxLength(100);
                });
            }
        }
        """;

    [Fact]
    public void SetTable_EntityConfiguredWithoutToTable_InsertsStatementAtEndOfBlock()
    {
        var result = new OnModelCreatingRewriter()
            .SetTable(SourceWithEntityConfiguredNoTable, entityName: "Person", tableName: "People", schema: "dbo");

        Assert.Contains("entity.ToTable(\"People\", \"dbo\")", result);
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(100)", result);
    }

    [Fact]
    public void SetTable_UnknownEntity_InsertsNewEntityBlock()
    {
        var result = new OnModelCreatingRewriter()
            .SetTable(TableMappingSource, entityName: "Vehicle", tableName: "Vehicles", schema: null);

        Assert.Contains("modelBuilder.Entity<Vehicle>(entity =>", result);
        Assert.Contains("entity.ToTable(\"Vehicles\")", result);

        var configs = new FluentConfigParser().ParseTableMappings(result).Value;
        Assert.Contains(configs, c => c is { EntityName: "Vehicle", TableName: "Vehicles", Schema: null });
        Assert.Contains(configs, c => c is { EntityName: "Person", TableName: "People", Schema: "dbo" });
    }

    [Fact]
    public void RemoveTable_ExistingCall_RemovesStatementEntirely()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveTable(TableMappingSource, entityName: "Person");

        Assert.DoesNotContain("ToTable", result);
    }

    [Fact]
    public void RemoveTable_NoMatchingCall_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveTable(SourceWithEntityConfiguredNoTable, entityName: "Person");

        Assert.Equal(SourceWithEntityConfiguredNoTable, result);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: build error — `OnModelCreatingRewriter` has no methods `SetTable`/`RemoveTable`.

- [ ] **Step 3: Implement**

Add these methods to the `OnModelCreatingRewriter` class (place them after `RemoveKey`, i.e. right before `public string AddEntity(...)`):

```csharp
    public string SetTable(string sourceCode, string entityName, string tableName, string? schema)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingToTableCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "ToTable"))
            .FirstOrDefault();

        if (existingToTableCall is not null)
        {
            return MutateExistingTable(root, existingToTableCall, tableName, schema);
        }

        var existingEntityInvocation = entityInvocations.FirstOrDefault();

        if (existingEntityInvocation is not null)
        {
            return InsertTableStatement(root, existingEntityInvocation, tableName, schema);
        }

        return InsertTableEntityBlock(root, entityName, tableName, schema);
    }

    private static string MutateExistingTable(CompilationUnitSyntax root, InvocationExpressionSyntax targetCall, string tableName, string? schema)
    {
        var newCall = targetCall.WithArgumentList(BuildToTableArgumentList(tableName, schema));

        var newRoot = root.ReplaceNode(targetCall, newCall);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertTableStatement(CompilationUnitSyntax root, InvocationExpressionSyntax entityInvocation, string tableName, string? schema)
    {
        var lambda = (SimpleLambdaExpressionSyntax)entityInvocation.ArgumentList.Arguments.Single().Expression;
        var block = lambda.Block!;
        var blockReceiverName = lambda.Parameter.Identifier.Text;

        var newStatement = BuildToTableStatement(blockReceiverName, tableName, schema);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertTableEntityBlock(CompilationUnitSyntax root, string entityName, string tableName, string? schema)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var tableStatement = BuildToTableStatement("entity", tableName, schema);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(tableStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildToTableStatement(string blockReceiverName, string tableName, string? schema)
    {
        return SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(blockReceiverName),
                    SyntaxFactory.IdentifierName("ToTable")),
                BuildToTableArgumentList(tableName, schema)));
    }

    private static ArgumentListSyntax BuildToTableArgumentList(string tableName, string? schema)
    {
        var tableNameArg = SyntaxFactory.Argument(
            SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(tableName)));

        if (schema is null)
        {
            return SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(tableNameArg));
        }

        var schemaArg = SyntaxFactory.Argument(
            SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(schema)));

        return SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] { tableNameArg, schemaArg }));
    }

    public string RemoveTable(string sourceCode, string entityName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingToTableCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "ToTable"))
            .FirstOrDefault();

        if (existingToTableCall is null || existingToTableCall.Parent is not ExpressionStatementSyntax statement)
        {
            return sourceCode;
        }

        var newRoot = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!;
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Add OnModelCreatingRewriter.SetTable/RemoveTable"
```

---

### Task 8: `OnModelCreatingRewriter.SetColumnName`/`RemoveColumnName`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Produces: `OnModelCreatingRewriter.SetColumnName(string sourceCode, string entityName, string propertyName, string columnName) : string`, `RemoveColumnName(string sourceCode, string entityName, string propertyName) : string`.

- [ ] **Step 1: Write the failing test**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`:

```csharp
    private const string ColumnNameSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasColumnName("full_name");
                    entity.Property(e => e.Email).HasColumnName("email_address");
                });
            }
        }
        """;

    [Fact]
    public void SetColumnName_ExistingCall_MutatesArgument()
    {
        var result = new OnModelCreatingRewriter()
            .SetColumnName(ColumnNameSource, entityName: "Person", propertyName: "Name", columnName: "display_name");

        Assert.Contains("entity.Property(e => e.Name).HasColumnName(\"display_name\")", result);
        Assert.Contains("entity.Property(e => e.Email).HasColumnName(\"email_address\")", result);
        Assert.DoesNotContain("HasColumnName(\"full_name\")", result);
    }

    private const string SourceWithPropertyButNoColumnName = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name);
                });
            }
        }
        """;

    [Fact]
    public void SetColumnName_BarePropertyCall_AppendsHasColumnName()
    {
        var result = new OnModelCreatingRewriter()
            .SetColumnName(SourceWithPropertyButNoColumnName, entityName: "Person", propertyName: "Name", columnName: "full_name");

        Assert.Contains("entity.Property(e => e.Name).HasColumnName(\"full_name\")", result);
    }

    [Fact]
    public void SetColumnName_UnknownEntity_InsertsNewEntityBlock()
    {
        var result = new OnModelCreatingRewriter()
            .SetColumnName(ColumnNameSource, entityName: "Vehicle", propertyName: "Vin", columnName: "vin_number");

        Assert.Contains("modelBuilder.Entity<Vehicle>(entity =>", result);
        Assert.Contains("entity.Property(e => e.Vin).HasColumnName(\"vin_number\")", result);
    }

    [Fact]
    public void RemoveColumnName_ExistingCall_RemovesCall_LeavesBarePropertyCall()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveColumnName(ColumnNameSource, entityName: "Person", propertyName: "Name");

        Assert.Contains("entity.Property(e => e.Name);", result);
        Assert.DoesNotContain("HasColumnName(\"full_name\")", result);
        Assert.Contains("entity.Property(e => e.Email).HasColumnName(\"email_address\")", result);
    }

    [Fact]
    public void RemoveColumnName_NoMatchingCall_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveColumnName(SourceWithPropertyButNoColumnName, entityName: "Person", propertyName: "Name");

        Assert.Equal(SourceWithPropertyButNoColumnName, result);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: build error — `OnModelCreatingRewriter` has no methods `SetColumnName`/`RemoveColumnName`.

- [ ] **Step 3: Implement**

Add these methods to the `OnModelCreatingRewriter` class (place them after `RemoveTable`):

```csharp
    public string SetColumnName(string sourceCode, string entityName, string propertyName, string columnName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasColumnName"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingCall is not null)
        {
            return MutateExistingStringArgCall(root, existingCall, columnName);
        }

        var existingPropertyCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "Property"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == propertyName);

        if (existingPropertyCall is not null)
        {
            return AppendStringArgCallToPropertyCall(root, existingPropertyCall, "HasColumnName", columnName);
        }

        var existingEntityInvocation = entityInvocations.FirstOrDefault();

        if (existingEntityInvocation is not null)
        {
            return InsertStringArgPropertyStatement(root, existingEntityInvocation, propertyName, "HasColumnName", columnName);
        }

        return InsertStringArgEntityBlock(root, entityName, propertyName, "HasColumnName", columnName);
    }

    public string RemoveColumnName(string sourceCode, string entityName, string propertyName)
    {
        return RemoveStringArgCall(sourceCode, entityName, propertyName, "HasColumnName");
    }
```

Now add four **shared** private helpers used by `SetColumnName` above and by `SetColumnType`/`SetDefaultValue` in Tasks 9–10 (place them directly after the two methods just added — they are written once here and reused, not duplicated per config kind):

```csharp
    private static string MutateExistingStringArgCall(CompilationUnitSyntax root, InvocationExpressionSyntax targetCall, string value)
    {
        var newArgument = SyntaxFactory.Argument(
            SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(value)));

        var newCall = targetCall.WithArgumentList(
            targetCall.ArgumentList.WithArguments(
                SyntaxFactory.SingletonSeparatedList(newArgument)));

        var newRoot = root.ReplaceNode(targetCall, newCall);
        return newRoot.ToFullString();
    }

    private static string AppendStringArgCallToPropertyCall(CompilationUnitSyntax root, InvocationExpressionSyntax propertyCall, string methodName, string value)
    {
        var newCall = BuildStringArgCall(propertyCall, methodName, value);

        var newRoot = root.ReplaceNode(propertyCall, newCall);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertStringArgPropertyStatement(CompilationUnitSyntax root, InvocationExpressionSyntax entityInvocation, string propertyName, string methodName, string value)
    {
        var lambda = (SimpleLambdaExpressionSyntax)entityInvocation.ArgumentList.Arguments.Single().Expression;
        var block = lambda.Block!;
        var blockReceiverName = lambda.Parameter.Identifier.Text;
        var propertyLambdaParam = FluentSyntaxHelpers.GetPropertyLambdaParameterName(entityInvocation);

        var newStatement = BuildStringArgPropertyStatement(blockReceiverName, propertyLambdaParam, propertyName, methodName, value);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertStringArgEntityBlock(CompilationUnitSyntax root, string entityName, string propertyName, string methodName, string value)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var propertyStatement = BuildStringArgPropertyStatement("entity", "e", propertyName, methodName, value);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(propertyStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildStringArgPropertyStatement(string blockReceiverName, string propertyLambdaParam, string propertyName, string methodName, string value)
    {
        var propertyCall = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(blockReceiverName),
                SyntaxFactory.IdentifierName("Property")),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(
                        SyntaxFactory.SimpleLambdaExpression(
                            SyntaxFactory.Parameter(SyntaxFactory.Identifier(propertyLambdaParam)),
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName(propertyLambdaParam),
                                SyntaxFactory.IdentifierName(propertyName)))))));

        return SyntaxFactory.ExpressionStatement(BuildStringArgCall(propertyCall, methodName, value));
    }

    private static InvocationExpressionSyntax BuildStringArgCall(ExpressionSyntax propertyCallExpression, string methodName, string value)
    {
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                propertyCallExpression,
                SyntaxFactory.IdentifierName(methodName)),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(
                        SyntaxFactory.LiteralExpression(
                            SyntaxKind.StringLiteralExpression,
                            SyntaxFactory.Literal(value))))));
    }

    private static string RemoveStringArgCall(string sourceCode, string entityName, string propertyName, string methodName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, methodName))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingCall is null)
        {
            return sourceCode;
        }

        var propertyCallExpression = ((MemberAccessExpressionSyntax)existingCall.Expression).Expression;

        var newRoot = root.ReplaceNode(existingCall, propertyCallExpression);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

`SetColumnType` (Task 9) and `SetDefaultValue`/`RemoveDefaultValue` (Task 10) call these same six shared helpers with a different `methodName`/value — do not duplicate them.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Add OnModelCreatingRewriter.SetColumnName/RemoveColumnName and shared string-arg rewrite helpers"
```

---

### Task 9: `OnModelCreatingRewriter.SetColumnType`/`RemoveColumnType`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: the six shared private helpers added in Task 8 (`MutateExistingStringArgCall`, `AppendStringArgCallToPropertyCall`, `InsertStringArgPropertyStatement`, `InsertStringArgEntityBlock`, `BuildStringArgPropertyStatement`, `BuildStringArgCall`, `RemoveStringArgCall`).
- Produces: `OnModelCreatingRewriter.SetColumnType(string sourceCode, string entityName, string propertyName, string columnType) : string`, `RemoveColumnType(string sourceCode, string entityName, string propertyName) : string`.

- [ ] **Step 1: Write the failing test**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`:

```csharp
    private const string ColumnTypeSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.Property(e => e.Total).HasColumnType("decimal(18,2)");
                });
            }
        }
        """;

    [Fact]
    public void SetColumnType_ExistingCall_MutatesArgument()
    {
        var result = new OnModelCreatingRewriter()
            .SetColumnType(ColumnTypeSource, entityName: "Order", propertyName: "Total", columnType: "money");

        Assert.Contains("entity.Property(e => e.Total).HasColumnType(\"money\")", result);
        Assert.DoesNotContain("HasColumnType(\"decimal(18,2)\")", result);
    }

    private const string SourceWithPropertyButNoColumnType = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.Property(e => e.Total);
                });
            }
        }
        """;

    [Fact]
    public void SetColumnType_BarePropertyCall_AppendsHasColumnType()
    {
        var result = new OnModelCreatingRewriter()
            .SetColumnType(SourceWithPropertyButNoColumnType, entityName: "Order", propertyName: "Total", columnType: "decimal(18,2)");

        Assert.Contains("entity.Property(e => e.Total).HasColumnType(\"decimal(18,2)\")", result);
    }

    [Fact]
    public void SetColumnType_UnknownEntity_InsertsNewEntityBlock()
    {
        var result = new OnModelCreatingRewriter()
            .SetColumnType(ColumnTypeSource, entityName: "Vehicle", propertyName: "Price", columnType: "money");

        Assert.Contains("modelBuilder.Entity<Vehicle>(entity =>", result);
        Assert.Contains("entity.Property(e => e.Price).HasColumnType(\"money\")", result);
    }

    [Fact]
    public void RemoveColumnType_ExistingCall_RemovesCall_LeavesBarePropertyCall()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveColumnType(ColumnTypeSource, entityName: "Order", propertyName: "Total");

        Assert.Contains("entity.Property(e => e.Total);", result);
        Assert.DoesNotContain("HasColumnType(\"decimal(18,2)\")", result);
    }

    [Fact]
    public void RemoveColumnType_NoMatchingCall_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveColumnType(SourceWithPropertyButNoColumnType, entityName: "Order", propertyName: "Total");

        Assert.Equal(SourceWithPropertyButNoColumnType, result);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: build error — `OnModelCreatingRewriter` has no methods `SetColumnType`/`RemoveColumnType`.

- [ ] **Step 3: Implement**

Add these methods to the `OnModelCreatingRewriter` class (place them after `RemoveColumnName`):

```csharp
    public string SetColumnType(string sourceCode, string entityName, string propertyName, string columnType)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasColumnType"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingCall is not null)
        {
            return MutateExistingStringArgCall(root, existingCall, columnType);
        }

        var existingPropertyCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "Property"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == propertyName);

        if (existingPropertyCall is not null)
        {
            return AppendStringArgCallToPropertyCall(root, existingPropertyCall, "HasColumnType", columnType);
        }

        var existingEntityInvocation = entityInvocations.FirstOrDefault();

        if (existingEntityInvocation is not null)
        {
            return InsertStringArgPropertyStatement(root, existingEntityInvocation, propertyName, "HasColumnType", columnType);
        }

        return InsertStringArgEntityBlock(root, entityName, propertyName, "HasColumnType", columnType);
    }

    public string RemoveColumnType(string sourceCode, string entityName, string propertyName)
    {
        return RemoveStringArgCall(sourceCode, entityName, propertyName, "HasColumnType");
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Add OnModelCreatingRewriter.SetColumnType/RemoveColumnType"
```

---

### Task 10: `OnModelCreatingRewriter.SetDefaultValue`/`RemoveDefaultValue`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: the shared private helpers from Task 8, except `MutateExistingStringArgCall`/`BuildStringArgCall` build a *string literal* argument — `HasDefaultValue`'s argument must be spliced in as raw expression syntax instead (it may be numeric/bool/null, not just string), so this task adds two small dedicated helpers rather than reusing the string-literal ones.
- Produces: `OnModelCreatingRewriter.SetDefaultValue(string sourceCode, string entityName, string propertyName, string literalText) : string`, `RemoveDefaultValue(string sourceCode, string entityName, string propertyName) : string`.

- [ ] **Step 1: Write the failing test**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`:

```csharp
    private const string DefaultValueSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.Property(e => e.Quantity).HasDefaultValue(1);
                    entity.Property(e => e.Status).HasDefaultValue("pending");
                });
            }
        }
        """;

    [Fact]
    public void SetDefaultValue_ExistingCall_MutatesArgument()
    {
        var result = new OnModelCreatingRewriter()
            .SetDefaultValue(DefaultValueSource, entityName: "Order", propertyName: "Quantity", literalText: "5");

        Assert.Contains("entity.Property(e => e.Quantity).HasDefaultValue(5)", result);
        Assert.Contains("entity.Property(e => e.Status).HasDefaultValue(\"pending\")", result);
        Assert.DoesNotContain("HasDefaultValue(1)", result);
    }

    [Fact]
    public void SetDefaultValue_ExistingCall_MutatesStringLiteralArgument()
    {
        var result = new OnModelCreatingRewriter()
            .SetDefaultValue(DefaultValueSource, entityName: "Order", propertyName: "Status", literalText: "\"active\"");

        Assert.Contains("entity.Property(e => e.Status).HasDefaultValue(\"active\")", result);
        Assert.DoesNotContain("HasDefaultValue(\"pending\")", result);
    }

    private const string SourceWithPropertyButNoDefaultValue = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Order>(entity =>
                {
                    entity.Property(e => e.Quantity);
                });
            }
        }
        """;

    [Fact]
    public void SetDefaultValue_BarePropertyCall_AppendsHasDefaultValue()
    {
        var result = new OnModelCreatingRewriter()
            .SetDefaultValue(SourceWithPropertyButNoDefaultValue, entityName: "Order", propertyName: "Quantity", literalText: "1");

        Assert.Contains("entity.Property(e => e.Quantity).HasDefaultValue(1)", result);
    }

    [Fact]
    public void SetDefaultValue_UnknownEntity_InsertsNewEntityBlock()
    {
        var result = new OnModelCreatingRewriter()
            .SetDefaultValue(DefaultValueSource, entityName: "Vehicle", propertyName: "Wheels", literalText: "4");

        Assert.Contains("modelBuilder.Entity<Vehicle>(entity =>", result);
        Assert.Contains("entity.Property(e => e.Wheels).HasDefaultValue(4)", result);
    }

    [Fact]
    public void RemoveDefaultValue_ExistingCall_RemovesCall_LeavesBarePropertyCall()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveDefaultValue(DefaultValueSource, entityName: "Order", propertyName: "Quantity");

        Assert.Contains("entity.Property(e => e.Quantity);", result);
        Assert.DoesNotContain("HasDefaultValue(1)", result);
        Assert.Contains("entity.Property(e => e.Status).HasDefaultValue(\"pending\")", result);
    }

    [Fact]
    public void RemoveDefaultValue_NoMatchingCall_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveDefaultValue(SourceWithPropertyButNoDefaultValue, entityName: "Order", propertyName: "Quantity");

        Assert.Equal(SourceWithPropertyButNoDefaultValue, result);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: build error — `OnModelCreatingRewriter` has no methods `SetDefaultValue`/`RemoveDefaultValue`.

- [ ] **Step 3: Implement**

Add these methods to the `OnModelCreatingRewriter` class (place them after `RemoveColumnType`):

```csharp
    public string SetDefaultValue(string sourceCode, string entityName, string propertyName, string literalText)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasDefaultValue"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingCall is not null)
        {
            return MutateExistingDefaultValue(root, existingCall, literalText);
        }

        var existingPropertyCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "Property"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == propertyName);

        if (existingPropertyCall is not null)
        {
            return AppendDefaultValueToPropertyCall(root, existingPropertyCall, literalText);
        }

        var existingEntityInvocation = entityInvocations.FirstOrDefault();

        if (existingEntityInvocation is not null)
        {
            return InsertDefaultValuePropertyStatement(root, existingEntityInvocation, propertyName, literalText);
        }

        return InsertDefaultValueEntityBlock(root, entityName, propertyName, literalText);
    }

    private static string MutateExistingDefaultValue(CompilationUnitSyntax root, InvocationExpressionSyntax targetCall, string literalText)
    {
        var newCall = targetCall.WithArgumentList(BuildDefaultValueArgumentList(literalText));

        var newRoot = root.ReplaceNode(targetCall, newCall);
        return newRoot.ToFullString();
    }

    private static string AppendDefaultValueToPropertyCall(CompilationUnitSyntax root, InvocationExpressionSyntax propertyCall, string literalText)
    {
        var newCall = BuildDefaultValueCall(propertyCall, literalText);

        var newRoot = root.ReplaceNode(propertyCall, newCall);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertDefaultValuePropertyStatement(CompilationUnitSyntax root, InvocationExpressionSyntax entityInvocation, string propertyName, string literalText)
    {
        var lambda = (SimpleLambdaExpressionSyntax)entityInvocation.ArgumentList.Arguments.Single().Expression;
        var block = lambda.Block!;
        var blockReceiverName = lambda.Parameter.Identifier.Text;
        var propertyLambdaParam = FluentSyntaxHelpers.GetPropertyLambdaParameterName(entityInvocation);

        var newStatement = BuildDefaultValuePropertyStatement(blockReceiverName, propertyLambdaParam, propertyName, literalText);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertDefaultValueEntityBlock(CompilationUnitSyntax root, string entityName, string propertyName, string literalText)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var propertyStatement = BuildDefaultValuePropertyStatement("entity", "e", propertyName, literalText);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(propertyStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildDefaultValuePropertyStatement(string blockReceiverName, string propertyLambdaParam, string propertyName, string literalText)
    {
        var propertyCall = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                SyntaxFactory.IdentifierName(blockReceiverName),
                SyntaxFactory.IdentifierName("Property")),
            SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(
                        SyntaxFactory.SimpleLambdaExpression(
                            SyntaxFactory.Parameter(SyntaxFactory.Identifier(propertyLambdaParam)),
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName(propertyLambdaParam),
                                SyntaxFactory.IdentifierName(propertyName)))))));

        return SyntaxFactory.ExpressionStatement(BuildDefaultValueCall(propertyCall, literalText));
    }

    private static InvocationExpressionSyntax BuildDefaultValueCall(ExpressionSyntax propertyCallExpression, string literalText)
    {
        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                propertyCallExpression,
                SyntaxFactory.IdentifierName("HasDefaultValue")),
            BuildDefaultValueArgumentList(literalText));
    }

    private static ArgumentListSyntax BuildDefaultValueArgumentList(string literalText)
    {
        var expression = SyntaxFactory.ParseExpression(literalText);

        return SyntaxFactory.ArgumentList(
            SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Argument(expression)));
    }

    public string RemoveDefaultValue(string sourceCode, string entityName, string propertyName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityInvocations = FluentSyntaxHelpers.FindEntityConfigInvocations(root, entityName).ToList();

        var existingCall = entityInvocations
            .SelectMany(entityInvocation => FluentSyntaxHelpers.FindCallsNamed(entityInvocation, "HasDefaultValue"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingCall is null)
        {
            return sourceCode;
        }

        var propertyCallExpression = ((MemberAccessExpressionSyntax)existingCall.Expression).Expression;

        var newRoot = root.ReplaceNode(existingCall, propertyCallExpression);
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

`BuildDefaultValueArgumentList` uses `SyntaxFactory.ParseExpression(literalText)` rather than always constructing a `LiteralExpressionSyntax` — `literalText` may be `"5"`, `"\"pending\""`, `"false"`, or `"null"`, and `ParseExpression` correctly re-parses any of these back into the right expression node, splicing the caller-supplied source text in verbatim (per the spec, `literalText` is only ever expected to come from `DefaultValueConfig.LiteralText` or a caller following that same contract).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 5: Run the full test suite**

Run: `dotnet test`
Expected: PASS, all tests across the whole solution green (confirms no regression in `HasMaxLength`/`HasKey`/`HasIndex`/etc.).

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Add OnModelCreatingRewriter.SetDefaultValue/RemoveDefaultValue"
```

---

### Task 11: Update the backlog

**Files:**
- Modify: `docs/backlog.md`

- [ ] **Step 1: Check off the column/table mapping item**

In `docs/backlog.md`, find this line in the Priority 2 section:

```
- [ ] **`[spec]` Column/table mapping** — `ToTable`, `HasColumnName`, `HasColumnType`, default values.
```

Replace it with:

```
- [x] **`[spec]` Column/table mapping** — `ToTable`, `HasColumnName`, `HasColumnType`, default values.
      **Update:** `FluentConfigParser.ParseTableMappings`/`ParseColumnNames`/
      `ParseColumnTypes`/`ParseDefaultValues` read `ToTable(name[, schema])`,
      `HasColumnName(...)`, `HasColumnType(...)`, and `HasDefaultValue(...)`
      (literals only) into four separate config DTOs;
      `ModelMerger.ApplyTableMapping`/`ApplyColumnNames`/`ApplyColumnTypes`/
      `ApplyDefaultValues` fold them into `EntityModel.TableName`/`Schema`
      and `PropertyModel.ColumnName`/`ColumnType`/`DefaultValueLiteral`;
      `OnModelCreatingRewriter.SetTable`/`SetColumnName`/`SetColumnType`/
      `SetDefaultValue` (and their `Remove*` counterparts) write it back,
      reusing the `HasKey`/`HasMaxLength` dispatch patterns (see
      `2026-07-12-column-table-mapping-config-design.md`). `HasDefaultValueSql`
      and non-literal defaults remain out of scope.
```

- [ ] **Step 2: Commit**

```bash
git add docs/backlog.md
git commit -m "Check off column/table mapping backlog item"
```

---

## Self-Review Notes

- **Spec coverage:** `EntityModel.TableName`/`Schema`, `PropertyModel.ColumnName`/`ColumnType`/`DefaultValueLiteral`, all four config DTOs, all four `ParseX` methods (including `ToTable`'s two-arg schema form and each kind's non-literal-argument diagnostic: `UnreadableToTableArgument`, `UnreadableHasColumnNameArgument`, `UnreadableHasColumnTypeArgument`, `UnreadableHasDefaultValueArgument`), all four `Apply*` merge methods, and all four `Set*`/`Remove*` rewrite pairs (table mapping's three-case dispatch; the three per-property mappings' four-case dispatch) are each covered by a task and at least one test per dispatch branch. Spec's "Out of scope" items (SQL/column validation, `HasDefaultValueSql`, non-literal defaults, `[Table]`/`[Column]` attributes, UI) are correctly not implemented.
- **Placeholder scan:** no TBD/TODO; every step shows complete code.
- **Type consistency:** `TableConfig.TableName`/`Schema` ↔ `EntityModel.TableName`/`Schema` ↔ `SetTable`'s `tableName`/`schema` parameters; `ColumnNameConfig.ColumnName` ↔ `PropertyModel.ColumnName` ↔ `SetColumnName`'s `columnName` parameter; same pattern for `ColumnType` and `DefaultValueLiteral`/`LiteralText` — all `string`/`string?` end-to-end, matching the spec. Task 8's six shared helpers are reused verbatim (not re-declared) by Task 9, avoiding drift between `SetColumnName` and `SetColumnType`.
- **Task ordering note:** Task 8 must be completed before Task 9, since Task 9 calls Task 8's shared private helpers rather than duplicating them — this is a within-file code dependency, not just a suggested order.
