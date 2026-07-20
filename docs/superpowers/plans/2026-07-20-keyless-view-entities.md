# Keyless / View Entities Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Parse EF Core's `HasNoKey()`/`[Keyless]`, `ToView(...)`, and `ToSqlQuery(...)` into the diagram model, surface them as editable fields in the diagram UI, and write edits back to source via new rewriter methods.

**Architecture:** Follows this codebase's established parse → merge → rewrite → `DiagramEditor` → `EntityNode.razor` pipeline exactly, adding three new entity-level `EntityModel` fields (`IsKeyless`, `ViewName`, `SqlQuery`) and the parser/merger/rewriter/editor/UI methods that read and write them. `HasNoKey()`/`HasKey()` are enforced as mutually exclusive at the rewriter layer (setting one strips the other); `ToTable`/`ToView` are not cross-cleared, matching a documented scope decision in the design spec.

**Tech Stack:** C# / Roslyn (`Microsoft.CodeAnalysis.CSharp`) for parsing and code generation, Blazor WebAssembly for the diagram UI, xUnit for tests.

## Global Constraints

- Every new file follows this codebase's existing namespace conventions: DTOs in `EfSchemaVisualizer.Core.Merging`, parsers in `EfSchemaVisualizer.Core.Parsing`, rewriter methods in `EfSchemaVisualizer.Core.CodeGen`.
- `ImplicitUsings` is enabled project-wide — do not add redundant `using System;`/`using System.Linq;` etc. beyond what a file already has.
- Run the full test suite with `dotnet test EfSchemaVisualizer.slnx` from the repo root after every task.
- Reference design: `docs/superpowers/specs/2026-07-20-keyless-view-entities-design.md`.

---

### Task 1: Model fields + Merging DTOs + ModelMerger methods

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`
- Create: `src/EfSchemaVisualizer.Core/Merging/ViewConfig.cs`
- Create: `src/EfSchemaVisualizer.Core/Merging/SqlQueryConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`

**Interfaces:**
- Produces: `EntityModel` gains `bool IsKeyless = false`, `string? ViewName = null`, `string? SqlQuery = null`. `ViewConfig(string EntityName, string ViewName, string? Schema)`. `SqlQueryConfig(string EntityName, string Sql)`. `ModelMerger.ApplyViewMapping(EntityModel, IReadOnlyList<ViewConfig>) -> EntityModel`. `ModelMerger.ApplySqlQuery(EntityModel, IReadOnlyList<SqlQueryConfig>) -> EntityModel`. `DiagnosticCodes.UnreadableToViewArgument`, `DiagnosticCodes.UnreadableToSqlQueryArgument`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`, right after the existing `ApplyTableMapping_NoMatchingConfig_LeavesTableNameAndSchemaNull` test:

```csharp
[Fact]
public void ApplyViewMapping_SetsViewNameAndSchema_OnMatchingEntity()
{
    var entity = new EntityModel("Person", new List<PropertyModel>());

    var configs = new List<ViewConfig>
    {
        new("Person", "PeopleView", "dbo"),
        new("Address", "AddressesView", null), // different entity, must not affect Person
    };

    var merged = ModelMerger.ApplyViewMapping(entity, configs);

    Assert.Equal("PeopleView", merged.ViewName);
    Assert.Equal("dbo", merged.Schema);
}

[Fact]
public void ApplyViewMapping_NoMatchingConfig_LeavesViewNameNull()
{
    var entity = new EntityModel("Person", new List<PropertyModel>());

    var merged = ModelMerger.ApplyViewMapping(entity, new List<ViewConfig>());

    Assert.Null(merged.ViewName);
}

[Fact]
public void ApplySqlQuery_SetsSqlQuery_OnMatchingEntity()
{
    var entity = new EntityModel("Person", new List<PropertyModel>());

    var configs = new List<SqlQueryConfig>
    {
        new("Person", "SELECT * FROM People"),
        new("Address", "SELECT * FROM Addresses"), // different entity, must not affect Person
    };

    var merged = ModelMerger.ApplySqlQuery(entity, configs);

    Assert.Equal("SELECT * FROM People", merged.SqlQuery);
}

[Fact]
public void ApplySqlQuery_NoMatchingConfig_LeavesSqlQueryNull()
{
    var entity = new EntityModel("Person", new List<PropertyModel>());

    var merged = ModelMerger.ApplySqlQuery(entity, new List<SqlQueryConfig>());

    Assert.Null(merged.SqlQuery);
}
```

- [ ] **Step 2: Run tests to verify they fail to compile**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~ModelMergerTests"`
Expected: build error — `ViewConfig`, `SqlQueryConfig`, `ApplyViewMapping`, `ApplySqlQuery` do not exist yet.

- [ ] **Step 3: Add the model fields**

In `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`, replace the whole record declaration:

```csharp
public sealed record EntityModel(
    string Name,
    IReadOnlyList<PropertyModel> Properties,
    IReadOnlyList<string>? KeyPropertyNames = null,
    IReadOnlyList<IndexModel>? Indexes = null,
    string? TableName = null,
    string? Schema = null,
    bool IsKeyless = false,
    string? ViewName = null,
    string? SqlQuery = null)
{
    public IReadOnlyList<string> KeyPropertyNames { get; init; } = KeyPropertyNames ?? new List<string>();
    public IReadOnlyList<IndexModel> Indexes { get; init; } = Indexes ?? new List<IndexModel>();
}
```

- [ ] **Step 4: Add the DTOs**

Create `src/EfSchemaVisualizer.Core/Merging/ViewConfig.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Merging;

public sealed record ViewConfig(string EntityName, string ViewName, string? Schema);
```

Create `src/EfSchemaVisualizer.Core/Merging/SqlQueryConfig.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Merging;

public sealed record SqlQueryConfig(string EntityName, string Sql);
```

- [ ] **Step 5: Add the diagnostic codes**

In `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`, add two new constants after `UnreadableHasDefaultValueArgument`:

```csharp
    public const string UnreadableToViewArgument = nameof(UnreadableToViewArgument);
    public const string UnreadableToSqlQueryArgument = nameof(UnreadableToSqlQueryArgument);
```

- [ ] **Step 6: Add the ModelMerger methods**

In `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`, add these two methods right after `ApplyTableMapping`:

```csharp
    public static EntityModel ApplyViewMapping(EntityModel entity, IReadOnlyList<ViewConfig> configs)
    {
        var config = configs.FirstOrDefault(c => c.EntityName == entity.Name);

        return config is null ? entity : entity with { ViewName = config.ViewName, Schema = config.Schema };
    }

    public static EntityModel ApplySqlQuery(EntityModel entity, IReadOnlyList<SqlQueryConfig> configs)
    {
        var config = configs.FirstOrDefault(c => c.EntityName == entity.Name);

        return config is null ? entity : entity with { SqlQuery = config.Sql };
    }
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~ModelMergerTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 8: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Model/EntityModel.cs src/EfSchemaVisualizer.Core/Merging/ViewConfig.cs src/EfSchemaVisualizer.Core/Merging/SqlQueryConfig.cs src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs
git commit -m "Add IsKeyless/ViewName/SqlQuery to EntityModel with merge support"
```

---

### Task 2: FluentConfigParser — ParseViewMappings, ParseSqlQueries, ParseKeylessEntities

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`

**Interfaces:**
- Consumes: `ViewConfig`, `SqlQueryConfig` (Task 1). `FluentSyntaxHelpers.FindConfigurationScopes(CompilationUnitSyntax) -> IEnumerable<(string EntityName, SyntaxNode Scope)>` and `FluentSyntaxHelpers.FindCallsNamed(SyntaxNode scope, string methodName) -> IEnumerable<InvocationExpressionSyntax>` (both pre-existing, used unchanged).
- Produces: `FluentConfigParser.ParseViewMappings(string sourceCode) -> ParseResult<IReadOnlyList<ViewConfig>>`. `FluentConfigParser.ParseSqlQueries(string sourceCode) -> ParseResult<IReadOnlyList<SqlQueryConfig>>`. `FluentConfigParser.ParseKeylessEntities(string sourceCode) -> IReadOnlyList<string>`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`, right after the existing `ParseTableMappings_NonLiteralArgument_EmitsUnreadableToTableArgumentDiagnostic` test (near line 993):

```csharp
    private const string ViewMappingSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.ToView("PeopleView", "dbo");
                });

                modelBuilder.Entity<Address>(entity =>
                {
                    entity.ToView("AddressesView");
                });
            }
        }
        """;

    [Fact]
    public void ParseViewMappings_ReadsViewNameOnly_AndViewNameWithSchema()
    {
        var result = new FluentConfigParser().ParseViewMappings(ViewMappingSource);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Value.Count);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", ViewName: "PeopleView", Schema: "dbo" });
        Assert.Contains(result.Value, c => c is { EntityName: "Address", ViewName: "AddressesView", Schema: null });
    }

    private const string ViewMappingSourceWithNonLiteralArg = """
        public class AppDbContext : DbContext
        {
            private const string ViewName = "PeopleView";

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.ToView(ViewName);
                });
            }
        }
        """;

    [Fact]
    public void ParseViewMappings_NonLiteralArgument_EmitsUnreadableToViewArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParseViewMappings(ViewMappingSourceWithNonLiteralArg);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnreadableToViewArgument, diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
        Assert.Null(diagnostic.PropertyName);
    }

    private const string SqlQuerySource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.ToSqlQuery("SELECT * FROM People");
                });
            }
        }
        """;

    [Fact]
    public void ParseSqlQueries_ReadsStringLiteralArgument()
    {
        var result = new FluentConfigParser().ParseSqlQueries(SqlQuerySource);

        Assert.Empty(result.Diagnostics);
        var config = Assert.Single(result.Value);
        Assert.Equal("Person", config.EntityName);
        Assert.Equal("SELECT * FROM People", config.Sql);
    }

    private const string SqlQuerySourceWithNonLiteralArg = """
        public class AppDbContext : DbContext
        {
            private const string Query = "SELECT * FROM People";

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.ToSqlQuery(Query);
                });
            }
        }
        """;

    [Fact]
    public void ParseSqlQueries_NonLiteralArgument_EmitsUnreadableToSqlQueryArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParseSqlQueries(SqlQuerySourceWithNonLiteralArg);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnreadableToSqlQueryArgument, diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
    }

    private const string KeylessSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.HasNoKey();
                });

                modelBuilder.Entity<Address>(entity =>
                {
                    entity.HasKey(e => e.Id);
                });
            }
        }
        """;

    [Fact]
    public void ParseKeylessEntities_ReadsEntityWithHasNoKeyCall()
    {
        var result = new FluentConfigParser().ParseKeylessEntities(KeylessSource);

        Assert.Equal(new[] { "Person" }, result);
    }

    [Fact]
    public void ParseKeylessEntities_NoHasNoKeyCalls_ReturnsEmpty()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity => { entity.HasKey(e => e.Id); });
                }
            }
            """;

        var result = new FluentConfigParser().ParseKeylessEntities(source);

        Assert.Empty(result);
    }
```

Also add, right after the existing `ParseUnrecognizedCalls_KnownChainsIncludingIsUniqueAndWithMany_AreNotFlagged` test:

```csharp
    [Fact]
    public void ParseUnrecognizedCalls_ToViewToSqlQueryHasNoKey_AreNotFlagged()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.ToView("PeopleView");
                        entity.HasNoKey();
                    });

                    modelBuilder.Entity<Address>(entity =>
                    {
                        entity.ToSqlQuery("SELECT * FROM Addresses");
                    });
                }
            }
            """;

        var diagnostics = new FluentConfigParser().ParseUnrecognizedCalls(source);

        Assert.Empty(diagnostics);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~FluentConfigParserTests"`
Expected: build error — `ParseViewMappings`, `ParseSqlQueries`, `ParseKeylessEntities` do not exist yet.

- [ ] **Step 3: Implement the three parser methods**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, first update `RecognizedCallNames` (near the top of the class) to add the three new call names:

```csharp
    private static readonly HashSet<string> RecognizedCallNames = new()
    {
        "Property", "HasMaxLength", "HasPrecision", "IsRequired", "HasKey", "ToTable",
        "HasColumnName", "HasColumnType", "HasDefaultValue", "HasIndex", "IsUnique",
        "HasOne", "HasMany", "WithOne", "WithMany", "HasForeignKey", "OnDelete", "UsingEntity",
        "Ignore", "ValueGeneratedOnAdd", "ValueGeneratedOnUpdate", "ValueGeneratedOnAddOrUpdate",
        "ValueGeneratedNever", "UseIdentityColumn", "ToView", "ToSqlQuery", "HasNoKey",
    };
```

Then add these three methods right after `ParseTableMappings` (after its closing brace, before `ParseColumnNames`):

```csharp
    public ParseResult<IReadOnlyList<ViewConfig>> ParseViewMappings(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<ViewConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var toViewCall in FluentSyntaxHelpers.FindCallsNamed(scope, "ToView"))
            {
                var arguments = toViewCall.ArgumentList.Arguments;

                if (arguments.Count == 0
                    || arguments[0].Expression is not LiteralExpressionSyntax { } viewNameLiteral
                    || !viewNameLiteral.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableToViewArgument,
                        "ToView argument is not a string literal and could not be read.",
                        entityName,
                        PropertyName: null,
                        toViewCall.Span));
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
                            DiagnosticCodes.UnreadableToViewArgument,
                            "ToView schema argument is not a string literal and could not be read.",
                            entityName,
                            PropertyName: null,
                            toViewCall.Span));
                        continue;
                    }
                }

                results.Add(new ViewConfig(entityName, viewNameLiteral.Token.ValueText, schema));
            }
        }

        return new ParseResult<IReadOnlyList<ViewConfig>>(results, diagnostics);
    }

    public ParseResult<IReadOnlyList<SqlQueryConfig>> ParseSqlQueries(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<SqlQueryConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var toSqlQueryCall in FluentSyntaxHelpers.FindCallsNamed(scope, "ToSqlQuery"))
            {
                var arg = toSqlQueryCall.ArgumentList.Arguments.FirstOrDefault();

                if (arg?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    results.Add(new SqlQueryConfig(entityName, literal.Token.ValueText));
                }
                else
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableToSqlQueryArgument,
                        "ToSqlQuery argument is not a string literal and could not be read.",
                        entityName,
                        PropertyName: null,
                        (arg ?? (SyntaxNode)toSqlQueryCall).Span));
                }
            }
        }

        return new ParseResult<IReadOnlyList<SqlQueryConfig>>(results, diagnostics);
    }

    /// Reads bare `entity.HasNoKey()` calls (no arguments to misparse), so unlike every other
    /// `Parse*` method here there is nothing that can fail to read — no diagnostic/`ParseResult`
    /// wrapper is needed, matching `ParseIgnoredEntities`'s precedent for the same reason.
    public IReadOnlyList<string> ParseKeylessEntities(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<string>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            if (FluentSyntaxHelpers.FindCallsNamed(scope, "HasNoKey").Any())
            {
                results.Add(entityName);
            }
        }

        return results.Distinct().ToList();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~FluentConfigParserTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Parse ToView, ToSqlQuery, and HasNoKey fluent calls"
```

---

### Task 3: EntityClassParser — `[Keyless]` attribute

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs`

**Interfaces:**
- Consumes: `FindAttribute(SyntaxList<AttributeListSyntax> attributeLists, string simpleName) -> AttributeSyntax?` (pre-existing, used unchanged).
- Produces: `EntityModel.IsKeyless` populated from a class-level `[Keyless]` attribute.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs`, right after the existing `Parse_TableAttribute_SetsTableNameAndSchema` test (near line 547):

```csharp
    [Fact]
    public void Parse_KeylessAttribute_SetsIsKeylessTrue()
    {
        const string source = """
            [Keyless]
            public class PersonView
            {
                public string Name { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.True(entity.IsKeyless);
    }

    [Fact]
    public void Parse_NoKeylessAttribute_IsKeylessFalse()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var entity = result.Value.Single();
        Assert.False(entity.IsKeyless);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~EntityClassParserTests"`
Expected: FAIL — `Parse_KeylessAttribute_SetsIsKeylessTrue` fails because `IsKeyless` is always `false` (the attribute is not read yet).

- [ ] **Step 3: Implement `[Keyless]` parsing**

In `src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs`, modify `ParseEntity`:

```csharp
    private static EntityModel ParseEntity(TypeDeclarationSyntax typeDeclaration)
    {
        var positionalProperties = typeDeclaration is RecordDeclarationSyntax
            ? typeDeclaration.ParameterList?.Parameters.Select(ParseParameterProperty) ?? Enumerable.Empty<PropertyModel>()
            : Enumerable.Empty<PropertyModel>();

        var mappedProperties = typeDeclaration.Members
            .OfType<PropertyDeclarationSyntax>()
            .Where(IsMappedInstanceProperty)
            .ToList();

        var bodyProperties = mappedProperties.Select(ParseProperty);

        var properties = positionalProperties.Concat(bodyProperties).ToList();

        var keyPropertyNames = ResolveKeyPropertyNames(mappedProperties);
        var (tableName, schema) = ParseTableAttribute(typeDeclaration.AttributeLists);
        var isKeyless = FindAttribute(typeDeclaration.AttributeLists, "Keyless") is not null;

        return new EntityModel(
            typeDeclaration.Identifier.Text,
            properties,
            keyPropertyNames,
            TableName: tableName,
            Schema: schema,
            IsKeyless: isKeyless);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~EntityClassParserTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs
git commit -m "Parse the [Keyless] data annotation attribute"
```

---

### Task 4: DiagramModelBuilder wiring

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs`

**Interfaces:**
- Consumes: `FluentConfigParser.ParseViewMappings`, `ParseSqlQueries`, `ParseKeylessEntities` (Task 2); `EntityClassParser`-produced `EntityModel.IsKeyless` (Task 3); `ModelMerger.ApplyViewMapping`, `ApplySqlQuery` (Task 1).
- Produces: `DiagramModelBuilder.Build` output `EntityModel`s carry `ViewName`/`Schema` from `ToView`, `SqlQuery` from `ToSqlQuery`, and `IsKeyless` true when either the `[Keyless]` attribute or a fluent `HasNoKey()` call is present (OR, not fluent-wins — there's no scalar value to conflict over).

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs`, at the end of the class (after the last existing test):

```csharp
    [Fact]
    public void Build_ToViewCall_SetsViewNameAndSchema()
    {
        const string classSource = """
            public class PersonView
            {
                public string Name { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<PersonView>(entity =>
                    {
                        entity.ToView("vPeople", "dbo");
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var entity = result.Entities.Single();
        Assert.Equal("vPeople", entity.ViewName);
        Assert.Equal("dbo", entity.Schema);
    }

    [Fact]
    public void Build_ToSqlQueryCall_SetsSqlQuery()
    {
        const string classSource = """
            public class PersonView
            {
                public string Name { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<PersonView>(entity =>
                    {
                        entity.ToSqlQuery("SELECT Name FROM People");
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.Equal("SELECT Name FROM People", result.Entities.Single().SqlQuery);
    }

    [Fact]
    public void Build_KeylessViaFluentHasNoKey_SetsIsKeylessTrue()
    {
        const string classSource = """
            public class PersonView
            {
                public string Name { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<PersonView>(entity =>
                    {
                        entity.HasNoKey();
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.True(result.Entities.Single().IsKeyless);
    }

    [Fact]
    public void Build_KeylessViaAttributeOnly_SetsIsKeylessTrue()
    {
        const string classSource = """
            [Keyless]
            public class PersonView
            {
                public string Name { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.True(result.Entities.Single().IsKeyless);
    }

    [Fact]
    public void Build_KeylessViaBothAttributeAndFluent_StillJustTrue()
    {
        const string classSource = """
            [Keyless]
            public class PersonView
            {
                public string Name { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<PersonView>(entity =>
                    {
                        entity.HasNoKey();
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.True(result.Entities.Single().IsKeyless);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~DiagramModelBuilderTests"`
Expected: FAIL — `ViewName`/`SqlQuery` stay null and `IsKeyless` stays false because `Build` doesn't call the new parsers/mergers yet.

- [ ] **Step 3: Wire the new parse/merge steps into `Build`**

In `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`, modify the `Build` method. First, add the three new parse calls after the existing `var tables = configParser.ParseTableMappings(configSource);` line:

```csharp
        var tables = configParser.ParseTableMappings(configSource);
        var views = configParser.ParseViewMappings(configSource);
        var sqlQueries = configParser.ParseSqlQueries(configSource);
        var fluentKeylessNames = configParser.ParseKeylessEntities(configSource).ToHashSet();
```

Then add their diagnostics after the existing `diagnostics.AddRange(tables.Diagnostics);` line:

```csharp
        diagnostics.AddRange(tables.Diagnostics);
        diagnostics.AddRange(views.Diagnostics);
        diagnostics.AddRange(sqlQueries.Diagnostics);
```

Then update the entities pipeline chain, adding three new `.Select(...)` calls right after the existing `.Select(entity => ModelMerger.ApplyTableMapping(entity, tables.Value))` line:

```csharp
            .Select(entity => ModelMerger.ApplyTableMapping(entity, tables.Value))
            .Select(entity => ModelMerger.ApplyViewMapping(entity, views.Value))
            .Select(entity => ModelMerger.ApplySqlQuery(entity, sqlQueries.Value))
            .Select(entity => entity.IsKeyless || fluentKeylessNames.Contains(entity.Name)
                ? entity with { IsKeyless = true }
                : entity)
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~DiagramModelBuilderTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 5: Run the full suite to catch regressions**

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: PASS, all tests across all three test projects green.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs
git commit -m "Merge view/SQL-query mapping and keyless status into the diagram model"
```

---

### Task 5: Rewriter — `SetView`/`RemoveView`, `SetSqlQuery`/`RemoveSqlQuery`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `FindConfigScopes(CompilationUnitSyntax, string entityName) -> List<SyntaxNode>`, `GetScopeBlockAndReceiver(SyntaxNode scope) -> (BlockSyntax Block, string ReceiverName)`, `FindOnModelCreatingMethod(CompilationUnitSyntax) -> MethodDeclarationSyntax`, `BuildEntityInvocationStatement(string modelBuilderParamName, string entityName, BlockSyntax block) -> ExpressionStatementSyntax` (all pre-existing private helpers on `OnModelCreatingRewriter`, used unchanged).
- Produces: `OnModelCreatingRewriter.SetView(string sourceCode, string entityName, string viewName, string? schema) -> string`. `RemoveView(string sourceCode, string entityName) -> string`. `SetSqlQuery(string sourceCode, string entityName, string sql) -> string`. `RemoveSqlQuery(string sourceCode, string entityName) -> string`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`, right after the existing `RemoveTable_NoMatchingCall_ReturnsSourceUnchanged` test (near line 1399):

```csharp
    private const string ViewMappingSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.ToView("PeopleView", "dbo");
                });
            }
        }
        """;

    [Fact]
    public void SetView_ExistingCall_MutatesArguments()
    {
        var result = new OnModelCreatingRewriter()
            .SetView(ViewMappingSource, entityName: "Person", viewName: "PersonsView", schema: "sales");

        Assert.Contains("entity.ToView(\"PersonsView\", \"sales\")", result);
        Assert.DoesNotContain("ToView(\"PeopleView\", \"dbo\")", result);
    }

    [Fact]
    public void SetView_ExistingCall_MutatesFromSchemaToNoSchema()
    {
        var result = new OnModelCreatingRewriter()
            .SetView(ViewMappingSource, entityName: "Person", viewName: "PersonsView", schema: null);

        Assert.Contains("entity.ToView(\"PersonsView\")", result);
        Assert.DoesNotContain("ToView(\"PeopleView\", \"dbo\")", result);
    }

    private const string SourceWithEntityConfiguredNoViewOrSqlQuery = """
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
    public void SetView_EntityConfiguredWithoutToView_InsertsStatementAtEndOfBlock()
    {
        var result = new OnModelCreatingRewriter()
            .SetView(SourceWithEntityConfiguredNoViewOrSqlQuery, entityName: "Person", viewName: "PeopleView", schema: "dbo");

        Assert.Contains("entity.ToView(\"PeopleView\", \"dbo\")", result);
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(100)", result);
    }

    [Fact]
    public void SetView_UnknownEntity_InsertsNewEntityBlock()
    {
        var result = new OnModelCreatingRewriter()
            .SetView(ViewMappingSource, entityName: "Vehicle", viewName: "VehiclesView", schema: null);

        Assert.Contains("modelBuilder.Entity<Vehicle>(entity =>", result);
        Assert.Contains("entity.ToView(\"VehiclesView\")", result);

        var configs = new FluentConfigParser().ParseViewMappings(result).Value;
        Assert.Contains(configs, c => c is { EntityName: "Vehicle", ViewName: "VehiclesView", Schema: null });
        Assert.Contains(configs, c => c is { EntityName: "Person", ViewName: "PeopleView", Schema: "dbo" });
    }

    [Fact]
    public void RemoveView_ExistingCall_RemovesStatementEntirely()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveView(ViewMappingSource, entityName: "Person");

        Assert.DoesNotContain("ToView", result);
    }

    [Fact]
    public void RemoveView_NoMatchingCall_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveView(SourceWithEntityConfiguredNoViewOrSqlQuery, entityName: "Person");

        Assert.Equal(SourceWithEntityConfiguredNoViewOrSqlQuery, result);
    }

    private const string SqlQueryMappingSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.ToSqlQuery("SELECT * FROM People");
                });
            }
        }
        """;

    [Fact]
    public void SetSqlQuery_ExistingCall_MutatesArgument()
    {
        var result = new OnModelCreatingRewriter()
            .SetSqlQuery(SqlQueryMappingSource, entityName: "Person", sql: "SELECT Id, Name FROM People");

        Assert.Contains("entity.ToSqlQuery(\"SELECT Id, Name FROM People\")", result);
        Assert.DoesNotContain("SELECT * FROM People", result);
    }

    [Fact]
    public void SetSqlQuery_EntityConfiguredWithoutToSqlQuery_InsertsStatementAtEndOfBlock()
    {
        var result = new OnModelCreatingRewriter()
            .SetSqlQuery(SourceWithEntityConfiguredNoViewOrSqlQuery, entityName: "Person", sql: "SELECT * FROM People");

        Assert.Contains("entity.ToSqlQuery(\"SELECT * FROM People\")", result);
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(100)", result);
    }

    [Fact]
    public void SetSqlQuery_UnknownEntity_InsertsNewEntityBlock()
    {
        var result = new OnModelCreatingRewriter()
            .SetSqlQuery(SqlQueryMappingSource, entityName: "Vehicle", sql: "SELECT * FROM Vehicles");

        Assert.Contains("modelBuilder.Entity<Vehicle>(entity =>", result);
        Assert.Contains("entity.ToSqlQuery(\"SELECT * FROM Vehicles\")", result);

        var configs = new FluentConfigParser().ParseSqlQueries(result).Value;
        Assert.Contains(configs, c => c.EntityName == "Vehicle" && c.Sql == "SELECT * FROM Vehicles");
        Assert.Contains(configs, c => c.EntityName == "Person" && c.Sql == "SELECT * FROM People");
    }

    [Fact]
    public void RemoveSqlQuery_ExistingCall_RemovesStatementEntirely()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveSqlQuery(SqlQueryMappingSource, entityName: "Person");

        Assert.DoesNotContain("ToSqlQuery", result);
    }

    [Fact]
    public void RemoveSqlQuery_NoMatchingCall_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveSqlQuery(SourceWithEntityConfiguredNoViewOrSqlQuery, entityName: "Person");

        Assert.Equal(SourceWithEntityConfiguredNoViewOrSqlQuery, result);
    }
```

- [ ] **Step 2: Run tests to verify they fail to compile**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: build error — `SetView`, `RemoveView`, `SetSqlQuery`, `RemoveSqlQuery` do not exist yet.

- [ ] **Step 3: Implement `SetView`/`RemoveView`**

In `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`, add these methods right after `RemoveTable` (after its closing brace, before `AddEntity`):

```csharp
    public string SetView(string sourceCode, string entityName, string viewName, string? schema)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingToViewCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "ToView"))
            .FirstOrDefault();

        if (existingToViewCall is not null)
        {
            return MutateExistingView(root, existingToViewCall, viewName, schema);
        }

        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            return InsertViewStatement(root, existingScope, viewName, schema);
        }

        return InsertViewEntityBlock(root, entityName, viewName, schema);
    }

    private static string MutateExistingView(CompilationUnitSyntax root, InvocationExpressionSyntax targetCall, string viewName, string? schema)
    {
        var newCall = targetCall.WithArgumentList(BuildToViewArgumentList(viewName, schema));

        var newRoot = root.ReplaceNode(targetCall, newCall);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertViewStatement(CompilationUnitSyntax root, SyntaxNode scope, string viewName, string? schema)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);

        var newStatement = BuildToViewStatement(blockReceiverName, viewName, schema);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertViewEntityBlock(CompilationUnitSyntax root, string entityName, string viewName, string? schema)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var viewStatement = BuildToViewStatement("entity", viewName, schema);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(viewStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildToViewStatement(string blockReceiverName, string viewName, string? schema)
    {
        return SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(blockReceiverName),
                    SyntaxFactory.IdentifierName("ToView")),
                BuildToViewArgumentList(viewName, schema)));
    }

    private static ArgumentListSyntax BuildToViewArgumentList(string viewName, string? schema)
    {
        var viewNameArg = SyntaxFactory.Argument(
            SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(viewName)));

        if (schema is null)
        {
            return SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(viewNameArg));
        }

        var schemaArg = SyntaxFactory.Argument(
            SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(schema)));

        return SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(new[] { viewNameArg, schemaArg }));
    }

    public string RemoveView(string sourceCode, string entityName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingToViewCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "ToView"))
            .FirstOrDefault();

        if (existingToViewCall is null || existingToViewCall.Parent is not ExpressionStatementSyntax statement)
        {
            return sourceCode;
        }

        var newRoot = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!;
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 4: Implement `SetSqlQuery`/`RemoveSqlQuery`**

Add these methods right after the `RemoveView` method (before `AddEntity`):

```csharp
    public string SetSqlQuery(string sourceCode, string entityName, string sql)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "ToSqlQuery"))
            .FirstOrDefault();

        if (existingCall is not null)
        {
            return MutateExistingSqlQuery(root, existingCall, sql);
        }

        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            return InsertSqlQueryStatement(root, existingScope, sql);
        }

        return InsertSqlQueryEntityBlock(root, entityName, sql);
    }

    private static string MutateExistingSqlQuery(CompilationUnitSyntax root, InvocationExpressionSyntax targetCall, string sql)
    {
        var newArgument = SyntaxFactory.Argument(
            SyntaxFactory.LiteralExpression(
                SyntaxKind.StringLiteralExpression,
                SyntaxFactory.Literal(sql)));

        var newCall = targetCall.WithArgumentList(
            targetCall.ArgumentList.WithArguments(
                SyntaxFactory.SingletonSeparatedList(newArgument)));

        var newRoot = root.ReplaceNode(targetCall, newCall);
        return newRoot.ToFullString();
    }

    private static string InsertSqlQueryStatement(CompilationUnitSyntax root, SyntaxNode scope, string sql)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);

        var newStatement = BuildToSqlQueryStatement(blockReceiverName, sql);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertSqlQueryEntityBlock(CompilationUnitSyntax root, string entityName, string sql)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var sqlQueryStatement = BuildToSqlQueryStatement("entity", sql);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(sqlQueryStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildToSqlQueryStatement(string blockReceiverName, string sql)
    {
        return SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(blockReceiverName),
                    SyntaxFactory.IdentifierName("ToSqlQuery")),
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(
                            SyntaxFactory.LiteralExpression(
                                SyntaxKind.StringLiteralExpression,
                                SyntaxFactory.Literal(sql)))))));
    }

    public string RemoveSqlQuery(string sourceCode, string entityName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "ToSqlQuery"))
            .FirstOrDefault();

        if (existingCall is null || existingCall.Parent is not ExpressionStatementSyntax statement)
        {
            return sourceCode;
        }

        var newRoot = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!;
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Add SetView/RemoveView and SetSqlQuery/RemoveSqlQuery rewriter methods"
```

---

### Task 6: Rewriter — `SetKeyless`/`RemoveKeyless` with `HasKey`/`HasNoKey` mutual exclusion

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: Same shared private helpers as Task 5, plus the existing `RemoveKey(string sourceCode, string entityName) -> string` (this task modifies `SetKey` to call `RemoveKeyless` first, and `RemoveKeyless` is defined in this task — see Step 4).
- Produces: `OnModelCreatingRewriter.SetKeyless(string sourceCode, string entityName) -> string`. `RemoveKeyless(string sourceCode, string entityName) -> string`. Modifies existing `SetKey` to strip any existing `HasNoKey()` call before writing the key.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`, right after the existing `RemoveKey_EntityHasNoConfigAtAll_ReturnsSourceUnchanged` test (near line 499):

```csharp
    [Fact]
    public void SetKeyless_EntityConfiguredWithoutHasNoKey_InsertsStatementAtEndOfBlock()
    {
        var result = new OnModelCreatingRewriter()
            .SetKeyless(SourceWithEntityConfiguredNoKey, entityName: "Person");

        Assert.Contains("entity.HasNoKey()", result);
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(100)", result);
    }

    [Fact]
    public void SetKeyless_UnknownEntity_InsertsNewEntityBlock()
    {
        var result = new OnModelCreatingRewriter()
            .SetKeyless(SourceWithSingleKey, entityName: "Vehicle");

        Assert.Contains("modelBuilder.Entity<Vehicle>", result);
        Assert.Contains("entity.HasNoKey()", result);
    }

    [Fact]
    public void SetKeyless_ExistingHasKeyCall_RemovesItAndInsertsHasNoKey()
    {
        var result = new OnModelCreatingRewriter()
            .SetKeyless(SourceWithSingleKey, entityName: "Person");

        Assert.DoesNotContain("HasKey", result);
        Assert.Contains("entity.HasNoKey()", result);
    }

    private const string SourceWithHasNoKey = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.HasNoKey();
                });
            }
        }
        """;

    [Fact]
    public void RemoveKeyless_ExistingCall_RemovesStatementEntirely()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveKeyless(SourceWithHasNoKey, entityName: "Person");

        Assert.DoesNotContain("HasNoKey", result);
    }

    [Fact]
    public void RemoveKeyless_NoMatchingCall_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter()
            .RemoveKeyless(SourceWithEntityConfiguredNoKey, entityName: "Person");

        Assert.Equal(SourceWithEntityConfiguredNoKey, result);
    }

    [Fact]
    public void SetKey_ExistingHasNoKeyCall_RemovesItAndInsertsHasKey()
    {
        var result = new OnModelCreatingRewriter()
            .SetKey(SourceWithHasNoKey, entityName: "Person", propertyNames: new List<string> { "Id" });

        Assert.DoesNotContain("HasNoKey", result);
        Assert.Contains("entity.HasKey(e => e.Id)", result);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: build error — `SetKeyless`, `RemoveKeyless` do not exist yet (and `SetKey_ExistingHasNoKeyCall_RemovesItAndInsertsHasKey` would fail once it compiles, since `SetKey` doesn't strip `HasNoKey` yet).

- [ ] **Step 3: Implement `SetKeyless`/`RemoveKeyless`**

In `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`, add these methods right after `RemoveKey` (after its closing brace, before `SetTable`):

```csharp
    public string SetKeyless(string sourceCode, string entityName)
    {
        var withoutKey = RemoveKey(sourceCode, entityName);

        var tree = CSharpSyntaxTree.ParseText(withoutKey);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingHasNoKeyCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasNoKey"))
            .FirstOrDefault();

        if (existingHasNoKeyCall is not null)
        {
            return withoutKey;
        }

        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            return InsertKeylessStatement(root, existingScope);
        }

        return InsertKeylessEntityBlock(root, entityName);
    }

    private static string InsertKeylessStatement(CompilationUnitSyntax root, SyntaxNode scope)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);

        var newStatement = BuildHasNoKeyStatement(blockReceiverName);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertKeylessEntityBlock(CompilationUnitSyntax root, string entityName)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var keylessStatement = BuildHasNoKeyStatement("entity");
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(keylessStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildHasNoKeyStatement(string blockReceiverName)
    {
        return SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(blockReceiverName),
                    SyntaxFactory.IdentifierName("HasNoKey")),
                SyntaxFactory.ArgumentList()));
    }

    public string RemoveKeyless(string sourceCode, string entityName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingHasNoKeyCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasNoKey"))
            .FirstOrDefault();

        if (existingHasNoKeyCall is null || existingHasNoKeyCall.Parent is not ExpressionStatementSyntax statement)
        {
            return sourceCode;
        }

        var newRoot = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!;
        return newRoot.NormalizeWhitespace().ToFullString();
    }
```

- [ ] **Step 4: Make `SetKey` strip an existing `HasNoKey()` call**

In `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`, modify the existing `SetKey` method's first two lines:

```csharp
    public string SetKey(string sourceCode, string entityName, IReadOnlyList<string> propertyNames)
    {
        var withoutKeyless = RemoveKeyless(sourceCode, entityName);

        var tree = CSharpSyntaxTree.ParseText(withoutKeyless);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);
```

(The rest of the method body — `existingHasKeyCall` lookup and dispatch — is unchanged; only the source it parses from changed, from `sourceCode` to `withoutKeyless`.)

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 6: Run the full suite to catch regressions**

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: PASS, all tests across all three test projects green (this confirms the `SetKey` change didn't break any existing `SetKey_*` test).

- [ ] **Step 7: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Add SetKeyless/RemoveKeyless with HasKey/HasNoKey mutual exclusion"
```

---

### Task 7: DiagramEditor — SetViewMapping, SetSqlQuery, SetKeyless

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`
- Create: `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorEntityMappingTests.cs`

**Interfaces:**
- Consumes: `OnModelCreatingRewriter.SetView`/`RemoveView`/`SetSqlQuery`/`RemoveSqlQuery`/`SetKeyless`/`RemoveKeyless` (Tasks 5–6). `DiagramEditResult.Ok()`/`Fail(string)` (pre-existing). Private `Apply(string newClassSource, string newConfigSource)` (pre-existing, used unchanged).
- Produces: `DiagramEditor.SetViewMapping(string entityName, string? viewName, string? schema) -> DiagramEditResult`. `SetSqlQuery(string entityName, string? sql) -> DiagramEditResult`. `SetKeyless(string entityName, bool isKeyless) -> DiagramEditResult`.

- [ ] **Step 1: Write the failing tests**

Create `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorEntityMappingTests.cs`:

```csharp
using EfSchemaVisualizer.Web.Diagram;

namespace EfSchemaVisualizer.Web.Tests.Diagram;

public class DiagramEditorEntityMappingTests
{
    private const string ClassSource = """
        public class Person
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
        }
        """;

    private const string ConfigSource = """
        modelBuilder.Entity<Person>(entity =>
        {
            entity.HasKey(e => e.Id);
        });
        """;

    [Fact]
    public void SetViewMapping_NoExistingConfig_InsertsToView()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetViewMapping("Person", "vPeople", "dbo");

        Assert.True(result.Success);
        Assert.Equal("vPeople", editor.Current.Entities.Single().ViewName);
        Assert.Equal("dbo", editor.Current.Entities.Single().Schema);
        Assert.Contains("ToView(\"vPeople\", \"dbo\")", editor.ConfigSource);
    }

    [Fact]
    public void SetViewMapping_ClearingExistingConfig_RemovesToView()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.SetViewMapping("Person", "vPeople", "dbo");

        var result = editor.SetViewMapping("Person", null, null);

        Assert.True(result.Success);
        Assert.Null(editor.Current.Entities.Single().ViewName);
        Assert.DoesNotContain("ToView", editor.ConfigSource);
    }

    [Fact]
    public void SetViewMapping_UnknownEntity_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetViewMapping("DoesNotExist", "vX", null);

        Assert.False(result.Success);
    }

    [Fact]
    public void SetSqlQuery_NoExistingConfig_InsertsToSqlQuery()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetSqlQuery("Person", "SELECT * FROM People");

        Assert.True(result.Success);
        Assert.Equal("SELECT * FROM People", editor.Current.Entities.Single().SqlQuery);
        Assert.Contains("ToSqlQuery(\"SELECT * FROM People\")", editor.ConfigSource);
    }

    [Fact]
    public void SetSqlQuery_ClearingExistingConfig_RemovesToSqlQuery()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.SetSqlQuery("Person", "SELECT * FROM People");

        var result = editor.SetSqlQuery("Person", null);

        Assert.True(result.Success);
        Assert.Null(editor.Current.Entities.Single().SqlQuery);
        Assert.DoesNotContain("ToSqlQuery", editor.ConfigSource);
    }

    [Fact]
    public void SetKeyless_SetToTrue_InsertsHasNoKeyAndRemovesHasKey()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetKeyless("Person", true);

        Assert.True(result.Success);
        Assert.True(editor.Current.Entities.Single().IsKeyless);
        Assert.Contains("HasNoKey()", editor.ConfigSource);
        Assert.DoesNotContain("HasKey", editor.ConfigSource);
    }

    [Fact]
    public void SetKeyless_SetToFalse_WhenAlreadyNotKeyless_IsNoOp()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetKeyless("Person", false);

        Assert.True(result.Success);
        Assert.Equal(ConfigSource, editor.ConfigSource);
    }

    [Fact]
    public void SetKeyless_UnknownEntity_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetKeyless("DoesNotExist", true);

        Assert.False(result.Success);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail to compile**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~DiagramEditorEntityMappingTests"`
Expected: build error — `SetViewMapping`, `SetSqlQuery`, `SetKeyless` do not exist on `DiagramEditor` yet.

- [ ] **Step 3: Implement the three `DiagramEditor` methods**

In `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`, add these methods right after `SetTableMapping` (after its closing brace, before `SetColumnName`):

```csharp
    public DiagramEditResult SetViewMapping(string entityName, string? viewName, string? schema)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var normalizedViewName = string.IsNullOrWhiteSpace(viewName) ? null : viewName.Trim();
        var normalizedSchema = string.IsNullOrWhiteSpace(schema) ? null : schema.Trim();

        if (normalizedViewName == entity.ViewName && normalizedSchema == entity.Schema)
        {
            return DiagramEditResult.Ok();
        }

        if (normalizedViewName is null)
        {
            var clearedConfigSource = _configRewriter.RemoveView(ConfigSource, entityName);
            Apply(ClassSource, clearedConfigSource);
            return DiagramEditResult.Ok();
        }

        var newConfigSource = _configRewriter.SetView(ConfigSource, entityName, normalizedViewName, normalizedSchema);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetSqlQuery(string entityName, string? sql)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var normalizedSql = string.IsNullOrWhiteSpace(sql) ? null : sql.Trim();

        if (normalizedSql == entity.SqlQuery)
        {
            return DiagramEditResult.Ok();
        }

        if (normalizedSql is null)
        {
            var clearedConfigSource = _configRewriter.RemoveSqlQuery(ConfigSource, entityName);
            Apply(ClassSource, clearedConfigSource);
            return DiagramEditResult.Ok();
        }

        var newConfigSource = _configRewriter.SetSqlQuery(ConfigSource, entityName, normalizedSql);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetKeyless(string entityName, bool isKeyless)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        if (isKeyless == entity.IsKeyless)
        {
            return DiagramEditResult.Ok();
        }

        var newConfigSource = isKeyless
            ? _configRewriter.SetKeyless(ConfigSource, entityName)
            : _configRewriter.RemoveKeyless(ConfigSource, entityName);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~DiagramEditorEntityMappingTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 5: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorEntityMappingTests.cs
git commit -m "Add DiagramEditor.SetViewMapping/SetSqlQuery/SetKeyless"
```

---

### Task 8: UI — EntityNode.razor fields for View, SQL query, Keyless

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`
- Modify: `tests/EfSchemaVisualizer.Web.Tests/Diagram/EntityNodeMarkupTests.cs`

**Interfaces:**
- Consumes: `DiagramEditor.SetViewMapping`, `SetSqlQuery`, `SetKeyless` (Task 7). `EntityModel.ViewName`, `SqlQuery`, `IsKeyless` (Task 1). Existing `EditContext.Editor`/`EditContext.NotifyChangedAsync()` pattern used by `CommitTableName`/`CommitSchema`.
- Produces: Three new header input fields (View, SQL query, Keyless checkbox) and a `disabled` state on the per-property primary-key checkbox when `Node.Entity.IsKeyless` is true.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Web.Tests/Diagram/EntityNodeMarkupTests.cs`, at the end of the class (after `PropertyRow_RendersReadOnlyShadowRow_WhenIsShadowIsSet`):

```csharp
    [Fact]
    public void EntityHeader_RendersViewAndSqlQueryAndKeylessFields()
    {
        var markup = ReadEntityNodeRazorSource();

        Assert.Contains("CommitViewName", markup);
        Assert.Contains("CommitSqlQuery", markup);
        Assert.Contains("CommitKeyless", markup);
    }

    [Fact]
    public void PrimaryKeyCheckbox_IsDisabled_WhenEntityIsKeyless()
    {
        var markup = ReadEntityNodeRazorSource();

        Assert.Contains("disabled=\"@Node.Entity.IsKeyless\"", markup);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~EntityNodeMarkupTests"`
Expected: FAIL — neither string appears in `EntityNode.razor` yet.

- [ ] **Step 3: Add the header fields**

In `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`, insert new markup right after the existing Table/Schema `<div>` block closes (after the line `</div>` that follows the Schema `<input>`, and before the `@if (_tableError is not null)` block):

```html
    <div style="padding: 2px 8px; font-size: 0.75em; color: #555; display: flex; align-items: center; gap: 4px;">
        <span title="Database view name (.ToView). Mutually exclusive with Table in EF, but not enforced here.">View:</span>
        <input style="width: 80px;" value="@Node.Entity.ViewName" placeholder="(none)"
               title="Database view name (.ToView). Mutually exclusive with Table in EF, but not enforced here."
               @onchange="e => CommitViewName(e.Value?.ToString())"
               @onpointerdown:stopPropagation="true"
               @onmousedown:stopPropagation="true" />
        <label style="margin-left: 8px;">
            <input type="checkbox" checked="@Node.Entity.IsKeyless"
                   @onchange="e => CommitKeyless((bool)(e.Value ?? false))"
                   @onpointerdown:stopPropagation="true"
                   @onmousedown:stopPropagation="true" />
            Keyless (no primary key)
        </label>
    </div>
    <div style="padding: 2px 8px; font-size: 0.75em; color: #555; display: flex; align-items: center; gap: 4px;">
        <span title="Raw SQL query source (.ToSqlQuery).">SQL query:</span>
        <input style="width: 200px;" value="@Node.Entity.SqlQuery" placeholder="(none)"
               title="Raw SQL query source (.ToSqlQuery)."
               @onchange="e => CommitSqlQuery(e.Value?.ToString())"
               @onpointerdown:stopPropagation="true"
               @onmousedown:stopPropagation="true" />
    </div>
    @if (_viewError is not null)
    {
        <div style="color: red; font-size: 0.8em; padding: 0 8px;">@_viewError</div>
    }
    @if (_sqlQueryError is not null)
    {
        <div style="color: red; font-size: 0.8em; padding: 0 8px;">@_sqlQueryError</div>
    }
```

- [ ] **Step 4: Disable the primary-key checkbox when the entity is keyless**

In the property expand panel, find this existing block:

```html
                            <label style="font-size: 0.8em; display: block;">
                                <input type="checkbox" checked="@Node.Entity.KeyPropertyNames.Contains(property.Name)"
                                       @onchange="e => ToggleKey(property, (bool)(e.Value ?? false))"
                                       @onpointerdown:stopPropagation="true"
                                       @onmousedown:stopPropagation="true" />
                                primary key
                            </label>
```

Replace it with:

```html
                            <label style="font-size: 0.8em; display: block;">
                                <input type="checkbox" checked="@Node.Entity.KeyPropertyNames.Contains(property.Name)"
                                       disabled="@Node.Entity.IsKeyless"
                                       @onchange="e => ToggleKey(property, (bool)(e.Value ?? false))"
                                       @onpointerdown:stopPropagation="true"
                                       @onmousedown:stopPropagation="true" />
                                primary key
                            </label>
```

- [ ] **Step 5: Add the code-behind commit handlers**

In the `@code` block, find the existing `CommitSchema` method and add these three methods right after it:

```csharp
    private string? _viewError;

    private async Task CommitViewName(string? newViewName)
    {
        var result = EditContext.Editor.SetViewMapping(Node.Entity.Name, newViewName, Node.Entity.Schema);
        if (result.Success)
        {
            _viewError = null;
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _viewError = result.Error;
        }
    }

    private string? _sqlQueryError;

    private async Task CommitSqlQuery(string? newSql)
    {
        var result = EditContext.Editor.SetSqlQuery(Node.Entity.Name, newSql);
        if (result.Success)
        {
            _sqlQueryError = null;
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _sqlQueryError = result.Error;
        }
    }

    private async Task CommitKeyless(bool isKeyless)
    {
        var result = EditContext.Editor.SetKeyless(Node.Entity.Name, isKeyless);
        if (!result.Success)
        {
            // SetKeyless only fails on an unknown entity, which can't happen from this UI
            // (the checkbox only renders for an entity already in the diagram) — nothing
            // to surface, matching how ToggleKey's analogous failure path is handled.
            return;
        }

        await EditContext.NotifyChangedAsync();
    }
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~EntityNodeMarkupTests"`
Expected: PASS, all tests in the file green.

- [ ] **Step 7: Run the full suite to catch regressions**

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: PASS, all tests across all three test projects green.

- [ ] **Step 8: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor tests/EfSchemaVisualizer.Web.Tests/Diagram/EntityNodeMarkupTests.cs
git commit -m "Show and edit View/SQL-query/Keyless fields in the diagram"
```

---

### Task 9: Update the backlog

**Files:**
- Modify: `docs/backlog.md`

**Interfaces:** None — documentation only.

- [ ] **Step 1: Mark the backlog item done**

In `docs/backlog.md`, find the line:

```markdown
- [ ] **`[found]` Keyless/view entities unread.** `HasNoKey()`, `ToView(...)`,
      `ToSqlQuery(...)`, `[Keyless]`. A scaffolded database-first project with
      views renders these as ordinary tables missing a key.
```

Replace it with:

```markdown
- [x] **`[found]` Keyless/view entities unread.** `HasNoKey()`, `ToView(...)`,
      `ToSqlQuery(...)`, `[Keyless]`. A scaffolded database-first project with
      views renders these as ordinary tables missing a key.
      **Update:** `FluentConfigParser.ParseViewMappings`/`ParseSqlQueries`/
      `ParseKeylessEntities` and `EntityClassParser`'s `[Keyless]` attribute
      handling feed `EntityModel.ViewName`/`Schema`/`SqlQuery`/`IsKeyless`;
      `OnModelCreatingRewriter.SetView`/`RemoveView`/`SetSqlQuery`/
      `RemoveSqlQuery`/`SetKeyless`/`RemoveKeyless` write it back, with
      `SetKeyless`/`SetKey` enforcing `HasNoKey`/`HasKey` mutual exclusion
      (a hard EF invariant) — see
      `2026-07-20-keyless-view-entities-design.md`. `ToTable`/`ToView` are
      deliberately not cross-cleared. `DiagramEditor.SetViewMapping`/
      `SetSqlQuery`/`SetKeyless` and new `EntityNode.razor` header fields
      (View, SQL query, Keyless checkbox, which also disables the
      per-property primary-key toggle) complete the edit path.
```

- [ ] **Step 2: Commit**

```bash
git add docs/backlog.md
git commit -m "Mark keyless/view entities backlog item done"
```
