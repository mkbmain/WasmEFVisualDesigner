# Smaller Unread Config Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Parse and merge eight previously-unread EF Core constructs (`HasQueryFilter`, `HasComment`, `IsUnicode`/`IsFixedLength`, `UseCollation`, `ToJson`, temporal tables, `SplitToTable`, `[InverseProperty]`) into `EntityModel`/`PropertyModel`, closing the Round 3 Priority 3 backlog item.

**Architecture:** Each construct follows the codebase's existing parse → DTO → `ModelMerger.Apply*` → `DiagramModelBuilder.Build` wiring pattern already used for every other config kind (see `HasColumnType`/`ApplyColumnTypes` as the template). Parse + merge only — no rewriter, no `DiagramEditor` methods, no diagram UI, matching the precedent set by relationships and value-generation.

**Tech Stack:** C#/.NET, Roslyn (`Microsoft.CodeAnalysis.CSharp`) syntax-tree parsing, xUnit.

## Global Constraints

- Parse + merge only — do not touch `OnModelCreatingRewriter`, `DiagramEditor`, or any `.razor` file in any task.
- Every new fluent call name added to `FluentConfigParser.RecognizedCallNames` in the same task that adds its parser, so it stops tripping the generic `UnrecognizedConfigCall` diagnostic the moment it's actually read.
- New optional fields on `PropertyModel`/`EntityModel` are appended after the existing fields, all with default values, so no existing positional-argument test construction breaks.
- `[DeleteBehavior]` is out of scope (not a real EF Core attribute — dropped during brainstorming).
- Follow this repo's existing code style exactly: file-scoped namespaces, `sealed record` DTOs, `IReadOnlyList<T>` for parser results, no comments beyond the doc-comment style already used on `Parse*`/`Apply*` methods where non-obvious.

---

### Task 1: `HasQueryFilter` — parse + merge

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Merging/QueryFilterConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Modify: `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`

**Interfaces:**
- Produces: `QueryFilterConfig(string EntityName)`; `FluentConfigParser.ParseQueryFilters(string sourceCode) : ParseResult<IReadOnlyList<QueryFilterConfig>>`; `ModelMerger.ApplyQueryFilters(EntityModel entity, IReadOnlyList<QueryFilterConfig> configs) : EntityModel`; `EntityModel.HasQueryFilter : bool`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs` (end of class, before final `}`):

```csharp
    private const string QueryFilterSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.HasQueryFilter(e => !e.IsDeleted);
                });
            }
        }
        """;

    [Fact]
    public void ParseQueryFilters_ReadsEntityWithHasQueryFilterCall()
    {
        var result = new FluentConfigParser().ParseQueryFilters(QueryFilterSource);

        Assert.Empty(result.Diagnostics);
        var config = Assert.Single(result.Value);
        Assert.Equal("Person", config.EntityName);
    }

    [Fact]
    public void ParseQueryFilters_NoHasQueryFilterCalls_ReturnsEmpty()
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

        var result = new FluentConfigParser().ParseQueryFilters(source);

        Assert.Empty(result.Value);
    }
```

Append to `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs` (end of class, before final `}`):

```csharp
    // ─── ApplyQueryFilters ─────────────────────────────────────────────────────────

    [Fact]
    public void ApplyQueryFilters_SetsFlagWhenEntityMatches_LeavesOtherEntitiesUntouched()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
        });

        var configs = new List<QueryFilterConfig>
        {
            new("Person"),
            new("Address"), // different entity, must not affect Person's own flag beyond matching
        };

        var merged = ModelMerger.ApplyQueryFilters(entity, configs);

        Assert.True(merged.HasQueryFilter);
    }

    [Fact]
    public void ApplyQueryFilters_NoMatchingConfig_LeavesFlagFalse()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
        });

        var merged = ModelMerger.ApplyQueryFilters(entity, new List<QueryFilterConfig> { new("Address") });

        Assert.False(merged.HasQueryFilter);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~ParseQueryFilters|FullyQualifiedName~ApplyQueryFilters"`
Expected: FAIL to compile (`ParseQueryFilters`, `QueryFilterConfig`, `ApplyQueryFilters`, `HasQueryFilter` don't exist yet).

- [ ] **Step 3: Create the DTO**

Create `src/EfSchemaVisualizer.Core/Merging/QueryFilterConfig.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Merging;

public sealed record QueryFilterConfig(string EntityName);
```

- [ ] **Step 4: Add the model field**

In `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`, add `bool HasQueryFilter = false` to the positional parameter list, after `IReadOnlyList<IReadOnlyList<string>>? AlternateKeys = null`:

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
    string? SqlQuery = null,
    IReadOnlyList<IReadOnlyList<string>>? AlternateKeys = null,
    bool HasQueryFilter = false)
```

- [ ] **Step 5: Add the parser method**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, add `"HasQueryFilter"` to `RecognizedCallNames`, and add this method after `ParseKeylessEntities` (same bare-marker-call shape, but wraps results in the DTO per the design spec rather than returning bare strings):

```csharp
    /// Reads bare `entity.HasQueryFilter(expr)` calls — presence only. The predicate expression
    /// itself can't be meaningfully read or fail to read, so there's nothing an
    /// "unreadable argument" diagnostic could report; matches `ParseKeylessEntities`'s reasoning
    /// for the same no-`ParseResult`-wrapper shape, but returns the DTO (not a bare string list)
    /// to match every other `Parse*` method's return shape.
    public ParseResult<IReadOnlyList<QueryFilterConfig>> ParseQueryFilters(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<QueryFilterConfig>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            if (FluentSyntaxHelpers.FindCallsNamed(scope, "HasQueryFilter").Any())
            {
                results.Add(new QueryFilterConfig(entityName));
            }
        }

        return new ParseResult<IReadOnlyList<QueryFilterConfig>>(results, new List<Diagnostic>());
    }
```

- [ ] **Step 6: Add the merge method**

In `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`, add after `ApplyAlternateKeys`:

```csharp
    public static EntityModel ApplyQueryFilters(EntityModel entity, IReadOnlyList<QueryFilterConfig> configs)
    {
        return configs.Any(c => c.EntityName == entity.Name)
            ? entity with { HasQueryFilter = true }
            : entity;
    }
```

- [ ] **Step 7: Wire into `DiagramModelBuilder.Build`**

In `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`, add after the `alternateKeys` line:

```csharp
        var queryFilters = configParser.ParseQueryFilters(configSource);
```

Add after `diagnostics.AddRange(alternateKeys.Diagnostics);`:

```csharp
        diagnostics.AddRange(queryFilters.Diagnostics);
```

Add after `.Select(entity => ModelMerger.ApplyAlternateKeys(entity, alternateKeys.Value))`:

```csharp
            .Select(entity => ModelMerger.ApplyQueryFilters(entity, queryFilters.Value))
```

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: PASS, all tests including the new ones.

- [ ] **Step 9: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Merging/QueryFilterConfig.cs src/EfSchemaVisualizer.Core/Model/EntityModel.cs src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs
git commit -m "Parse and merge HasQueryFilter presence into EntityModel"
```

---

### Task 2: `HasComment` (entity + property) — parse + merge

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Merging/EntityCommentConfig.cs`
- Create: `src/EfSchemaVisualizer.Core/Merging/PropertyCommentConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`
- Modify: `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Modify: `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.GetPropertyNameFor(InvocationExpressionSyntax) : string?` (existing, `internal`, same assembly).
- Produces: `EntityCommentConfig(string EntityName, string Comment)`; `PropertyCommentConfig(string EntityName, string PropertyName, string Comment)`; `FluentConfigParser.ParseComments(string sourceCode) : (ParseResult<IReadOnlyList<EntityCommentConfig>> Entities, ParseResult<IReadOnlyList<PropertyCommentConfig>> Properties)`; `ModelMerger.ApplyEntityComments`/`ApplyPropertyComments`; `EntityModel.Comment : string?`; `PropertyModel.Comment : string?`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`:

```csharp
    private const string CommentSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.HasComment("People in the system.");
                    entity.Property(e => e.Name).HasComment("Full display name.");
                });
            }
        }
        """;

    [Fact]
    public void ParseComments_ReadsEntityAndPropertyComments()
    {
        var (entities, properties) = new FluentConfigParser().ParseComments(CommentSource);

        Assert.Empty(entities.Diagnostics);
        Assert.Empty(properties.Diagnostics);

        var entityConfig = Assert.Single(entities.Value);
        Assert.Equal("Person", entityConfig.EntityName);
        Assert.Equal("People in the system.", entityConfig.Comment);

        var propertyConfig = Assert.Single(properties.Value);
        Assert.Equal("Person", propertyConfig.EntityName);
        Assert.Equal("Name", propertyConfig.PropertyName);
        Assert.Equal("Full display name.", propertyConfig.Comment);
    }

    private const string CommentSourceWithNonLiteralArg = """
        public class AppDbContext : DbContext
        {
            private const string Note = "Full display name.";

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).HasComment(Note);
                });
            }
        }
        """;

    [Fact]
    public void ParseComments_NonLiteralPropertyArgument_EmitsUnreadableHasCommentArgumentDiagnostic()
    {
        var (entities, properties) = new FluentConfigParser().ParseComments(CommentSourceWithNonLiteralArg);

        Assert.Empty(entities.Value);
        Assert.Empty(properties.Value);
        var diagnostic = Assert.Single(properties.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnreadableHasCommentArgument, diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
        Assert.Equal("Name", diagnostic.PropertyName);
    }
```

Append to `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`:

```csharp
    // ─── ApplyEntityComments / ApplyPropertyComments ──────────────────────────────

    [Fact]
    public void ApplyEntityComments_SetsCommentWhenEntityMatches()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>());

        var merged = ModelMerger.ApplyEntityComments(entity, new List<EntityCommentConfig>
        {
            new("Person", "People in the system."),
            new("Address", "Should not apply."),
        });

        Assert.Equal("People in the system.", merged.Comment);
    }

    [Fact]
    public void ApplyPropertyComments_SetsCommentOnMatchingProperty_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Name", "string", IsNullable: true, MaxLength: null),
        });

        var merged = ModelMerger.ApplyPropertyComments(entity, new List<PropertyCommentConfig>
        {
            new("Person", "Name", "Full display name."),
        });

        Assert.Null(merged.Properties.Single(p => p.Name == "Id").Comment);
        Assert.Equal("Full display name.", merged.Properties.Single(p => p.Name == "Name").Comment);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~ParseComments|FullyQualifiedName~ApplyEntityComments|FullyQualifiedName~ApplyPropertyComments"`
Expected: FAIL to compile.

- [ ] **Step 3: Create the DTOs**

Create `src/EfSchemaVisualizer.Core/Merging/EntityCommentConfig.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Merging;

public sealed record EntityCommentConfig(string EntityName, string Comment);
```

Create `src/EfSchemaVisualizer.Core/Merging/PropertyCommentConfig.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Merging;

public sealed record PropertyCommentConfig(string EntityName, string PropertyName, string Comment);
```

- [ ] **Step 4: Add the model fields**

In `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`, add `string? Comment = null` after `bool HasQueryFilter = false`:

```csharp
    bool HasQueryFilter = false,
    string? Comment = null)
```

In `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`, add `string? Comment = null` after `bool IsConcurrencyToken = false`:

```csharp
    bool IsConcurrencyToken = false,
    string? Comment = null);
```

- [ ] **Step 5: Add the diagnostic code**

In `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`, add before the closing brace:

```csharp
    public const string UnreadableHasCommentArgument = nameof(UnreadableHasCommentArgument);
```

- [ ] **Step 6: Add the parser method**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, add `"HasComment"` to `RecognizedCallNames`, and add this method after `ParseQueryFilters`:

```csharp
    /// `HasComment` is legal chained directly onto the entity receiver (entity-level comment) or
    /// onto a `.Property(...)` call (property-level comment). `GetPropertyNameFor` returning null
    /// is the existing signal, used elsewhere, for "this call isn't property-scoped" — reused here
    /// to route each call to the right result list instead of guessing from call shape.
    public (ParseResult<IReadOnlyList<EntityCommentConfig>> Entities, ParseResult<IReadOnlyList<PropertyCommentConfig>> Properties)
        ParseComments(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var entityResults = new List<EntityCommentConfig>();
        var entityDiagnostics = new List<Diagnostic>();
        var propertyResults = new List<PropertyCommentConfig>();
        var propertyDiagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "HasComment"))
            {
                var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(call);
                var arg = call.ArgumentList.Arguments.FirstOrDefault();
                var isReadableLiteral = arg?.Expression is LiteralExpressionSyntax literal
                    && literal.IsKind(SyntaxKind.StringLiteralExpression);

                if (propertyName is null)
                {
                    if (isReadableLiteral)
                    {
                        entityResults.Add(new EntityCommentConfig(
                            entityName, ((LiteralExpressionSyntax)arg!.Expression).Token.ValueText));
                    }
                    else
                    {
                        entityDiagnostics.Add(new Diagnostic(
                            DiagnosticCodes.UnreadableHasCommentArgument,
                            "HasComment argument is not a string literal and could not be read.",
                            entityName,
                            PropertyName: null,
                            (arg ?? (SyntaxNode)call).Span));
                    }
                }
                else
                {
                    if (isReadableLiteral)
                    {
                        propertyResults.Add(new PropertyCommentConfig(
                            entityName, propertyName, ((LiteralExpressionSyntax)arg!.Expression).Token.ValueText));
                    }
                    else
                    {
                        propertyDiagnostics.Add(new Diagnostic(
                            DiagnosticCodes.UnreadableHasCommentArgument,
                            "HasComment argument is not a string literal and could not be read.",
                            entityName,
                            propertyName,
                            (arg ?? (SyntaxNode)call).Span));
                    }
                }
            }
        }

        return (
            new ParseResult<IReadOnlyList<EntityCommentConfig>>(entityResults, entityDiagnostics),
            new ParseResult<IReadOnlyList<PropertyCommentConfig>>(propertyResults, propertyDiagnostics));
    }
```

- [ ] **Step 7: Add the merge methods**

In `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`, add after `ApplyQueryFilters`:

```csharp
    public static EntityModel ApplyEntityComments(EntityModel entity, IReadOnlyList<EntityCommentConfig> configs)
    {
        var config = configs.FirstOrDefault(c => c.EntityName == entity.Name);

        return config is null ? entity : entity with { Comment = config.Comment };
    }

    public static EntityModel ApplyPropertyComments(EntityModel entity, IReadOnlyList<PropertyCommentConfig> configs)
    {
        var byProperty = IndexByProperty(entity.Name, configs, c => c.EntityName, c => c.PropertyName);

        var updatedProperties = entity.Properties
            .Select(property => byProperty.TryGetValue(property.Name, out var config)
                ? property with { Comment = config.Comment }
                : property)
            .ToList();

        return entity with { Properties = updatedProperties };
    }
```

- [ ] **Step 8: Wire into `DiagramModelBuilder.Build`**

In `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`, add after the `queryFilters` line:

```csharp
        var comments = configParser.ParseComments(configSource);
```

Add after `diagnostics.AddRange(queryFilters.Diagnostics);`:

```csharp
        diagnostics.AddRange(comments.Entities.Diagnostics);
        diagnostics.AddRange(comments.Properties.Diagnostics);
```

Add after `.Select(entity => ModelMerger.ApplyQueryFilters(entity, queryFilters.Value))`:

```csharp
            .Select(entity => ModelMerger.ApplyEntityComments(entity, comments.Entities.Value))
            .Select(entity => ModelMerger.ApplyPropertyComments(entity, comments.Properties.Value))
```

- [ ] **Step 9: Run tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Merging/EntityCommentConfig.cs src/EfSchemaVisualizer.Core/Merging/PropertyCommentConfig.cs src/EfSchemaVisualizer.Core/Model/EntityModel.cs src/EfSchemaVisualizer.Core/Model/PropertyModel.cs src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs
git commit -m "Parse and merge entity- and property-level HasComment"
```

---

### Task 3: `IsUnicode` — parse + merge

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Merging/UnicodeConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Modify: `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`

**Interfaces:**
- Produces: `UnicodeConfig(string EntityName, string PropertyName, bool IsUnicode)`; `FluentConfigParser.ParseUnicodeFlags(string sourceCode) : ParseResult<IReadOnlyList<UnicodeConfig>>`; `ModelMerger.ApplyUnicodeFlags`; `PropertyModel.IsUnicode : bool?`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`:

```csharp
    private const string UnicodeSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).IsUnicode();
                    entity.Property(e => e.Code).IsUnicode(false);
                });
            }
        }
        """;

    [Fact]
    public void ParseUnicodeFlags_ReadsBareCallAsTrue_AndExplicitBoolArgument()
    {
        var result = new FluentConfigParser().ParseUnicodeFlags(UnicodeSource);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Name", IsUnicode: true });
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Code", IsUnicode: false });
    }

    private const string UnicodeSourceWithNonBoolArg = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).IsUnicode(1);
                });
            }
        }
        """;

    [Fact]
    public void ParseUnicodeFlags_NonBooleanArgument_EmitsUnreadableIsUnicodeArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParseUnicodeFlags(UnicodeSourceWithNonBoolArg);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnreadableIsUnicodeArgument, diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
        Assert.Equal("Name", diagnostic.PropertyName);
    }
```

Append to `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`:

```csharp
    // ─── ApplyUnicodeFlags ─────────────────────────────────────────────────────────

    [Fact]
    public void ApplyUnicodeFlags_SetsFlagOnMatchingProperty_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Name", "string", IsNullable: true, MaxLength: null),
        });

        var merged = ModelMerger.ApplyUnicodeFlags(entity, new List<UnicodeConfig>
        {
            new("Person", "Name", false),
        });

        Assert.Null(merged.Properties.Single(p => p.Name == "Id").IsUnicode);
        Assert.False(merged.Properties.Single(p => p.Name == "Name").IsUnicode);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~ParseUnicodeFlags|FullyQualifiedName~ApplyUnicodeFlags"`
Expected: FAIL to compile.

- [ ] **Step 3: Create the DTO**

Create `src/EfSchemaVisualizer.Core/Merging/UnicodeConfig.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Merging;

public sealed record UnicodeConfig(string EntityName, string PropertyName, bool IsUnicode);
```

- [ ] **Step 4: Add the model field**

In `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`, add `bool? IsUnicode = null` after `string? Comment = null`:

```csharp
    string? Comment = null,
    bool? IsUnicode = null);
```

- [ ] **Step 5: Add the diagnostic code**

In `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`, add:

```csharp
    public const string UnreadableIsUnicodeArgument = nameof(UnreadableIsUnicodeArgument);
```

- [ ] **Step 6: Add the parser method**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, add `"IsUnicode"` to `RecognizedCallNames`, and add this method (same shape as `ParseIsRequired`) after `ParseComments`:

```csharp
    public ParseResult<IReadOnlyList<UnicodeConfig>> ParseUnicodeFlags(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<UnicodeConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "IsUnicode"))
            {
                var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(call);

                if (propertyName is null)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnresolvablePropertyName,
                        "Could not determine which property this IsUnicode call configures.",
                        entityName,
                        PropertyName: null,
                        call.Span));
                    continue;
                }

                var arg = call.ArgumentList.Arguments.FirstOrDefault();

                if (arg is null)
                {
                    results.Add(new UnicodeConfig(entityName, propertyName, IsUnicode: true));
                    continue;
                }

                if (arg.Expression is LiteralExpressionSyntax literal
                    && (literal.IsKind(SyntaxKind.TrueLiteralExpression) || literal.IsKind(SyntaxKind.FalseLiteralExpression)))
                {
                    results.Add(new UnicodeConfig(entityName, propertyName, literal.IsKind(SyntaxKind.TrueLiteralExpression)));
                }
                else
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableIsUnicodeArgument,
                        "IsUnicode argument is not a boolean literal and could not be read.",
                        entityName,
                        propertyName,
                        arg.Span));
                }
            }
        }

        return new ParseResult<IReadOnlyList<UnicodeConfig>>(results, diagnostics);
    }
```

- [ ] **Step 7: Add the merge method**

In `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`, add after `ApplyPropertyComments`:

```csharp
    public static EntityModel ApplyUnicodeFlags(EntityModel entity, IReadOnlyList<UnicodeConfig> configs)
    {
        var byProperty = IndexByProperty(entity.Name, configs, c => c.EntityName, c => c.PropertyName);

        var updatedProperties = entity.Properties
            .Select(property => byProperty.TryGetValue(property.Name, out var config)
                ? property with { IsUnicode = config.IsUnicode }
                : property)
            .ToList();

        return entity with { Properties = updatedProperties };
    }
```

- [ ] **Step 8: Wire into `DiagramModelBuilder.Build`**

In `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`, add after the `comments` line:

```csharp
        var unicodeFlags = configParser.ParseUnicodeFlags(configSource);
```

Add after `diagnostics.AddRange(comments.Properties.Diagnostics);`:

```csharp
        diagnostics.AddRange(unicodeFlags.Diagnostics);
```

Add after `.Select(entity => ModelMerger.ApplyPropertyComments(entity, comments.Properties.Value))`:

```csharp
            .Select(entity => ModelMerger.ApplyUnicodeFlags(entity, unicodeFlags.Value))
```

- [ ] **Step 9: Run tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Merging/UnicodeConfig.cs src/EfSchemaVisualizer.Core/Model/PropertyModel.cs src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs
git commit -m "Parse and merge IsUnicode"
```

---

### Task 4: `IsFixedLength` — parse + merge

**Files:** Same file set as Task 3, substituting `FixedLength` for `Unicode` throughout.

**Interfaces:**
- Produces: `FixedLengthConfig(string EntityName, string PropertyName, bool IsFixedLength)`; `FluentConfigParser.ParseFixedLengthFlags(string sourceCode) : ParseResult<IReadOnlyList<FixedLengthConfig>>`; `ModelMerger.ApplyFixedLengthFlags`; `PropertyModel.IsFixedLength : bool?`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`:

```csharp
    private const string FixedLengthSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Code).IsFixedLength();
                    entity.Property(e => e.Name).IsFixedLength(false);
                });
            }
        }
        """;

    [Fact]
    public void ParseFixedLengthFlags_ReadsBareCallAsTrue_AndExplicitBoolArgument()
    {
        var result = new FluentConfigParser().ParseFixedLengthFlags(FixedLengthSource);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Code", IsFixedLength: true });
        Assert.Contains(result.Value, c => c is { EntityName: "Person", PropertyName: "Name", IsFixedLength: false });
    }

    private const string FixedLengthSourceWithNonBoolArg = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Code).IsFixedLength(1);
                });
            }
        }
        """;

    [Fact]
    public void ParseFixedLengthFlags_NonBooleanArgument_EmitsUnreadableIsFixedLengthArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParseFixedLengthFlags(FixedLengthSourceWithNonBoolArg);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnreadableIsFixedLengthArgument, diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
        Assert.Equal("Code", diagnostic.PropertyName);
    }
```

Append to `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`:

```csharp
    // ─── ApplyFixedLengthFlags ─────────────────────────────────────────────────────

    [Fact]
    public void ApplyFixedLengthFlags_SetsFlagOnMatchingProperty_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Code", "string", IsNullable: true, MaxLength: null),
        });

        var merged = ModelMerger.ApplyFixedLengthFlags(entity, new List<FixedLengthConfig>
        {
            new("Person", "Code", true),
        });

        Assert.Null(merged.Properties.Single(p => p.Name == "Id").IsFixedLength);
        Assert.True(merged.Properties.Single(p => p.Name == "Code").IsFixedLength);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~ParseFixedLengthFlags|FullyQualifiedName~ApplyFixedLengthFlags"`
Expected: FAIL to compile.

- [ ] **Step 3: Create the DTO**

Create `src/EfSchemaVisualizer.Core/Merging/FixedLengthConfig.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Merging;

public sealed record FixedLengthConfig(string EntityName, string PropertyName, bool IsFixedLength);
```

- [ ] **Step 4: Add the model field**

In `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`, add `bool? IsFixedLength = null` after `bool? IsUnicode = null`:

```csharp
    bool? IsUnicode = null,
    bool? IsFixedLength = null);
```

- [ ] **Step 5: Add the diagnostic code**

In `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`, add:

```csharp
    public const string UnreadableIsFixedLengthArgument = nameof(UnreadableIsFixedLengthArgument);
```

- [ ] **Step 6: Add the parser method**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, add `"IsFixedLength"` to `RecognizedCallNames`, and add this method after `ParseUnicodeFlags`:

```csharp
    public ParseResult<IReadOnlyList<FixedLengthConfig>> ParseFixedLengthFlags(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<FixedLengthConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "IsFixedLength"))
            {
                var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(call);

                if (propertyName is null)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnresolvablePropertyName,
                        "Could not determine which property this IsFixedLength call configures.",
                        entityName,
                        PropertyName: null,
                        call.Span));
                    continue;
                }

                var arg = call.ArgumentList.Arguments.FirstOrDefault();

                if (arg is null)
                {
                    results.Add(new FixedLengthConfig(entityName, propertyName, IsFixedLength: true));
                    continue;
                }

                if (arg.Expression is LiteralExpressionSyntax literal
                    && (literal.IsKind(SyntaxKind.TrueLiteralExpression) || literal.IsKind(SyntaxKind.FalseLiteralExpression)))
                {
                    results.Add(new FixedLengthConfig(entityName, propertyName, literal.IsKind(SyntaxKind.TrueLiteralExpression)));
                }
                else
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableIsFixedLengthArgument,
                        "IsFixedLength argument is not a boolean literal and could not be read.",
                        entityName,
                        propertyName,
                        arg.Span));
                }
            }
        }

        return new ParseResult<IReadOnlyList<FixedLengthConfig>>(results, diagnostics);
    }
```

- [ ] **Step 7: Add the merge method**

In `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`, add after `ApplyUnicodeFlags`:

```csharp
    public static EntityModel ApplyFixedLengthFlags(EntityModel entity, IReadOnlyList<FixedLengthConfig> configs)
    {
        var byProperty = IndexByProperty(entity.Name, configs, c => c.EntityName, c => c.PropertyName);

        var updatedProperties = entity.Properties
            .Select(property => byProperty.TryGetValue(property.Name, out var config)
                ? property with { IsFixedLength = config.IsFixedLength }
                : property)
            .ToList();

        return entity with { Properties = updatedProperties };
    }
```

- [ ] **Step 8: Wire into `DiagramModelBuilder.Build`**

In `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`, add after the `unicodeFlags` line:

```csharp
        var fixedLengthFlags = configParser.ParseFixedLengthFlags(configSource);
```

Add after `diagnostics.AddRange(unicodeFlags.Diagnostics);`:

```csharp
        diagnostics.AddRange(fixedLengthFlags.Diagnostics);
```

Add after `.Select(entity => ModelMerger.ApplyUnicodeFlags(entity, unicodeFlags.Value))`:

```csharp
            .Select(entity => ModelMerger.ApplyFixedLengthFlags(entity, fixedLengthFlags.Value))
```

- [ ] **Step 9: Run tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Merging/FixedLengthConfig.cs src/EfSchemaVisualizer.Core/Model/PropertyModel.cs src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs
git commit -m "Parse and merge IsFixedLength"
```

---

### Task 5: `UseCollation` — parse + merge

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Merging/CollationConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Modify: `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`

**Interfaces:**
- Produces: `CollationConfig(string EntityName, string PropertyName, string Collation)`; `FluentConfigParser.ParseCollations(string sourceCode) : ParseResult<IReadOnlyList<CollationConfig>>`; `ModelMerger.ApplyCollations`; `PropertyModel.Collation : string?`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`:

```csharp
    private const string CollationSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).UseCollation("SQL_Latin1_General_CP1_CI_AS");
                });
            }
        }
        """;

    [Fact]
    public void ParseCollations_ReadsCollationArgument()
    {
        var result = new FluentConfigParser().ParseCollations(CollationSource);

        Assert.Empty(result.Diagnostics);
        var config = Assert.Single(result.Value);
        Assert.Equal("Person", config.EntityName);
        Assert.Equal("Name", config.PropertyName);
        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", config.Collation);
    }

    private const string CollationSourceWithNonLiteralArg = """
        public class AppDbContext : DbContext
        {
            private const string Collation = "SQL_Latin1_General_CP1_CI_AS";

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.Property(e => e.Name).UseCollation(Collation);
                });
            }
        }
        """;

    [Fact]
    public void ParseCollations_NonLiteralArgument_EmitsUnreadableUseCollationArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParseCollations(CollationSourceWithNonLiteralArg);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnreadableUseCollationArgument, diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
        Assert.Equal("Name", diagnostic.PropertyName);
    }
```

Append to `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`:

```csharp
    // ─── ApplyCollations ───────────────────────────────────────────────────────────

    [Fact]
    public void ApplyCollations_SetsCollationOnMatchingProperty_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Name", "string", IsNullable: true, MaxLength: null),
        });

        var merged = ModelMerger.ApplyCollations(entity, new List<CollationConfig>
        {
            new("Person", "Name", "SQL_Latin1_General_CP1_CI_AS"),
        });

        Assert.Null(merged.Properties.Single(p => p.Name == "Id").Collation);
        Assert.Equal("SQL_Latin1_General_CP1_CI_AS", merged.Properties.Single(p => p.Name == "Name").Collation);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~ParseCollations|FullyQualifiedName~ApplyCollations"`
Expected: FAIL to compile.

- [ ] **Step 3: Create the DTO**

Create `src/EfSchemaVisualizer.Core/Merging/CollationConfig.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Merging;

public sealed record CollationConfig(string EntityName, string PropertyName, string Collation);
```

- [ ] **Step 4: Add the model field**

In `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`, add `string? Collation = null` after `bool? IsFixedLength = null`:

```csharp
    bool? IsFixedLength = null,
    string? Collation = null);
```

- [ ] **Step 5: Add the diagnostic code**

In `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`, add:

```csharp
    public const string UnreadableUseCollationArgument = nameof(UnreadableUseCollationArgument);
```

- [ ] **Step 6: Add the parser method**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, add `"UseCollation"` to `RecognizedCallNames`, and add this method (same shape as `ParseColumnTypes`) after `ParseFixedLengthFlags`:

```csharp
    public ParseResult<IReadOnlyList<CollationConfig>> ParseCollations(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<CollationConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "UseCollation"))
            {
                var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(call);

                if (propertyName is null)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnresolvablePropertyName,
                        "Could not determine which property this UseCollation call configures.",
                        entityName,
                        PropertyName: null,
                        call.Span));
                    continue;
                }

                var arg = call.ArgumentList.Arguments.FirstOrDefault();

                if (arg?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    results.Add(new CollationConfig(entityName, propertyName, literal.Token.ValueText));
                }
                else
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableUseCollationArgument,
                        "UseCollation argument is not a string literal and could not be read.",
                        entityName,
                        propertyName,
                        (arg ?? (SyntaxNode)call).Span));
                }
            }
        }

        return new ParseResult<IReadOnlyList<CollationConfig>>(results, diagnostics);
    }
```

- [ ] **Step 7: Add the merge method**

In `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`, add after `ApplyFixedLengthFlags`:

```csharp
    public static EntityModel ApplyCollations(EntityModel entity, IReadOnlyList<CollationConfig> configs)
    {
        var byProperty = IndexByProperty(entity.Name, configs, c => c.EntityName, c => c.PropertyName);

        var updatedProperties = entity.Properties
            .Select(property => byProperty.TryGetValue(property.Name, out var config)
                ? property with { Collation = config.Collation }
                : property)
            .ToList();

        return entity with { Properties = updatedProperties };
    }
```

- [ ] **Step 8: Wire into `DiagramModelBuilder.Build`**

In `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`, add after the `fixedLengthFlags` line:

```csharp
        var collations = configParser.ParseCollations(configSource);
```

Add after `diagnostics.AddRange(fixedLengthFlags.Diagnostics);`:

```csharp
        diagnostics.AddRange(collations.Diagnostics);
```

Add after `.Select(entity => ModelMerger.ApplyFixedLengthFlags(entity, fixedLengthFlags.Value))`:

```csharp
            .Select(entity => ModelMerger.ApplyCollations(entity, collations.Value))
```

- [ ] **Step 9: Run tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Merging/CollationConfig.cs src/EfSchemaVisualizer.Core/Model/PropertyModel.cs src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs
git commit -m "Parse and merge UseCollation"
```

---

### Task 6: `ToJson` — parse + merge

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Merging/JsonConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Modify: `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`

**Interfaces:**
- Produces: `JsonConfig(string EntityName, string? ColumnName)`; `FluentConfigParser.ParseJsonMappings(string sourceCode) : ParseResult<IReadOnlyList<JsonConfig>>`; `ModelMerger.ApplyJsonMappings`; `EntityModel.IsJson : bool`, `EntityModel.JsonColumnName : string?`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`:

```csharp
    private const string JsonSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.ToJson();
                });

                modelBuilder.Entity<Address>(entity =>
                {
                    entity.ToJson("address_json");
                });
            }
        }
        """;

    [Fact]
    public void ParseJsonMappings_ReadsBareCall_AndCallWithColumnName()
    {
        var result = new FluentConfigParser().ParseJsonMappings(JsonSource);

        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", ColumnName: null });
        Assert.Contains(result.Value, c => c is { EntityName: "Address", ColumnName: "address_json" });
    }

    private const string JsonSourceWithNonLiteralArg = """
        public class AppDbContext : DbContext
        {
            private const string ColumnName = "address_json";

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Address>(entity =>
                {
                    entity.ToJson(ColumnName);
                });
            }
        }
        """;

    [Fact]
    public void ParseJsonMappings_NonLiteralArgument_EmitsUnreadableToJsonArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParseJsonMappings(JsonSourceWithNonLiteralArg);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnreadableToJsonArgument, diagnostic.Code);
        Assert.Equal("Address", diagnostic.EntityName);
        Assert.Null(diagnostic.PropertyName);
    }
```

Append to `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`:

```csharp
    // ─── ApplyJsonMappings ─────────────────────────────────────────────────────────

    [Fact]
    public void ApplyJsonMappings_SetsIsJsonAndColumnName_WhenEntityMatches()
    {
        var entity = new EntityModel("Address", new List<PropertyModel>());

        var merged = ModelMerger.ApplyJsonMappings(entity, new List<JsonConfig>
        {
            new("Address", "address_json"),
            new("Person", null),
        });

        Assert.True(merged.IsJson);
        Assert.Equal("address_json", merged.JsonColumnName);
    }

    [Fact]
    public void ApplyJsonMappings_NoMatchingConfig_LeavesIsJsonFalse()
    {
        var entity = new EntityModel("Address", new List<PropertyModel>());

        var merged = ModelMerger.ApplyJsonMappings(entity, new List<JsonConfig> { new("Person", null) });

        Assert.False(merged.IsJson);
        Assert.Null(merged.JsonColumnName);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~ParseJsonMappings|FullyQualifiedName~ApplyJsonMappings"`
Expected: FAIL to compile.

- [ ] **Step 3: Create the DTO**

Create `src/EfSchemaVisualizer.Core/Merging/JsonConfig.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Merging;

public sealed record JsonConfig(string EntityName, string? ColumnName);
```

- [ ] **Step 4: Add the model fields**

In `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`, add `bool IsJson = false` and `string? JsonColumnName = null` after `string? Comment = null`:

```csharp
    string? Comment = null,
    bool IsJson = false,
    string? JsonColumnName = null)
```

- [ ] **Step 5: Add the diagnostic code**

In `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`, add:

```csharp
    public const string UnreadableToJsonArgument = nameof(UnreadableToJsonArgument);
```

- [ ] **Step 6: Add the parser method**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, add `"ToJson"` to `RecognizedCallNames`, and add this method after `ParseCollations`:

```csharp
    public ParseResult<IReadOnlyList<JsonConfig>> ParseJsonMappings(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<JsonConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "ToJson"))
            {
                var arg = call.ArgumentList.Arguments.FirstOrDefault();

                if (arg is null)
                {
                    results.Add(new JsonConfig(entityName, null));
                }
                else if (arg.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    results.Add(new JsonConfig(entityName, literal.Token.ValueText));
                }
                else
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableToJsonArgument,
                        "ToJson argument is not a string literal and could not be read.",
                        entityName,
                        PropertyName: null,
                        arg.Span));
                }
            }
        }

        return new ParseResult<IReadOnlyList<JsonConfig>>(results, diagnostics);
    }
```

- [ ] **Step 7: Add the merge method**

In `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`, add after `ApplyCollations`:

```csharp
    public static EntityModel ApplyJsonMappings(EntityModel entity, IReadOnlyList<JsonConfig> configs)
    {
        var config = configs.FirstOrDefault(c => c.EntityName == entity.Name);

        return config is null ? entity : entity with { IsJson = true, JsonColumnName = config.ColumnName };
    }
```

- [ ] **Step 8: Wire into `DiagramModelBuilder.Build`**

In `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`, add after the `collations` line:

```csharp
        var jsonMappings = configParser.ParseJsonMappings(configSource);
```

Add after `diagnostics.AddRange(collations.Diagnostics);`:

```csharp
        diagnostics.AddRange(jsonMappings.Diagnostics);
```

Add after `.Select(entity => ModelMerger.ApplyCollations(entity, collations.Value))`:

```csharp
            .Select(entity => ModelMerger.ApplyJsonMappings(entity, jsonMappings.Value))
```

- [ ] **Step 9: Run tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Merging/JsonConfig.cs src/EfSchemaVisualizer.Core/Model/EntityModel.cs src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs
git commit -m "Parse and merge ToJson entity mapping"
```

---

### Task 7: Temporal tables — extend `ParseTableMappings`

This task changes `ParseTableMappings`'s return type, so it touches every existing call site. Do this task carefully and run the full suite (not a filter) at the end.

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Merging/TemporalConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Modify: `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`
- Modify: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`
- Modify: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`
- Modify: `tests/EfSchemaVisualizer.Core.Tests/RoundTripFuzzTests.cs`

**Interfaces:**
- Produces: `TemporalConfig(string EntityName)`; `FluentConfigParser.ParseTableMappings(string sourceCode) : ParseResult<(IReadOnlyList<TableConfig> Tables, IReadOnlyList<TemporalConfig> Temporal)>` (**signature change** — was `ParseResult<IReadOnlyList<TableConfig>>`); `ModelMerger.ApplyTemporal`; `EntityModel.IsTemporal : bool`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`:

```csharp
    private const string TemporalSourceSingleArg = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.ToTable(b => b.IsTemporal());
                });
            }
        }
        """;

    [Fact]
    public void ParseTableMappings_ToTableWithSingleArgTemporalLambda_ReadsTemporalConfig_NoTableConfig()
    {
        var result = new FluentConfigParser().ParseTableMappings(TemporalSourceSingleArg);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.Value.Tables);
        var temporal = Assert.Single(result.Value.Temporal);
        Assert.Equal("Person", temporal.EntityName);
    }

    private const string TemporalSourceTwoArgs = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.ToTable("People", b => b.IsTemporal());
                });
            }
        }
        """;

    [Fact]
    public void ParseTableMappings_ToTableWithTableNameAndTemporalLambda_ReadsBothTableAndTemporalConfig()
    {
        var result = new FluentConfigParser().ParseTableMappings(TemporalSourceTwoArgs);

        Assert.Empty(result.Diagnostics);
        var table = Assert.Single(result.Value.Tables);
        Assert.Equal("Person", table.EntityName);
        Assert.Equal("People", table.TableName);
        var temporal = Assert.Single(result.Value.Temporal);
        Assert.Equal("Person", temporal.EntityName);
    }

    private const string NonTemporalConfigLambdaSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.ToTable(b => b.ExcludeFromMigrations());
                });
            }
        }
        """;

    [Fact]
    public void ParseTableMappings_ToTableConfigLambdaWithoutIsTemporal_ReadsNeitherTableNorTemporalConfig_NoDiagnostic()
    {
        var result = new FluentConfigParser().ParseTableMappings(NonTemporalConfigLambdaSource);

        Assert.Empty(result.Diagnostics);
        Assert.Empty(result.Value.Tables);
        Assert.Empty(result.Value.Temporal);
    }
```

Now update the two existing `ParseTableMappings` tests in the same file to the new `.Value.Tables` shape:

Find `ParseTableMappings_ReadsTableNameOnly_AndTableNameWithSchema` and change:
```csharp
        Assert.Equal(2, result.Value.Count);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", TableName: "People", Schema: "dbo" });
        Assert.Contains(result.Value, c => c is { EntityName: "Address", TableName: "Addresses", Schema: null });
```
to:
```csharp
        Assert.Equal(2, result.Value.Tables.Count);
        Assert.Contains(result.Value.Tables, c => c is { EntityName: "Person", TableName: "People", Schema: "dbo" });
        Assert.Contains(result.Value.Tables, c => c is { EntityName: "Address", TableName: "Addresses", Schema: null });
```

Find `ParseTableMappings_NonLiteralArgument_EmitsUnreadableToTableArgumentDiagnostic` and change:
```csharp
        Assert.Empty(result.Value);
```
to:
```csharp
        Assert.Empty(result.Value.Tables);
        Assert.Empty(result.Value.Temporal);
```

Find `ParseTableMappings_EntityTypeConfigurationStyle_ReadsConfiguredTable` and change:
```csharp
        var config = Assert.Single(result.Value);
```
to:
```csharp
        var config = Assert.Single(result.Value.Tables);
```

In `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`, find `SetTable_UnknownEntity_InsertsNewEntityBlock` and change:
```csharp
        var configs = new FluentConfigParser().ParseTableMappings(result).Value;
```
to:
```csharp
        var configs = new FluentConfigParser().ParseTableMappings(result).Value.Tables;
```

In `tests/EfSchemaVisualizer.Core.Tests/RoundTripFuzzTests.cs`, find the line in `NoOpEdits_AreByteIdenticalAcrossEveryConfigKindInTheCorpus`:
```csharp
        var table = parser.ParseTableMappings(ConfigSource).Value.Single(c => c.EntityName == "Blog");
```
and change to:
```csharp
        var table = parser.ParseTableMappings(ConfigSource).Value.Tables.Single(c => c.EntityName == "Blog");
```

- [ ] **Step 2: Run tests to verify the new ones fail (and confirm the whole solution currently fails to build)**

Run: `dotnet build EfSchemaVisualizer.slnx`
Expected: FAIL — `result.Value.Tables`/`result.Value.Temporal` don't exist on the current `IReadOnlyList<TableConfig>` return type.

- [ ] **Step 3: Create the DTO**

Create `src/EfSchemaVisualizer.Core/Merging/TemporalConfig.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Merging;

public sealed record TemporalConfig(string EntityName);
```

- [ ] **Step 4: Add the model field**

In `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`, add `bool IsTemporal = false` after `string? JsonColumnName = null`:

```csharp
    string? JsonColumnName = null,
    bool IsTemporal = false)
```

- [ ] **Step 5: Replace `ParseTableMappings`**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, replace the entire existing `ParseTableMappings` method body with:

```csharp
    /// Reads both `ToTable("Name"[, "schema"])` and the config-lambda overloads
    /// (`ToTable(b => b.IsTemporal())`, `ToTable("Name", b => b.IsTemporal())`) in one pass, since
    /// both read the same call name — a second full walk of every `ToTable` call would be
    /// redundant. Only `IsTemporal()` is recognized inside a config lambda; any other builder
    /// configuration inside it is not read and produces no diagnostic (same scope cut as
    /// `SplitToTable`'s builder-lambda internals), but a known table name (two-arg overload) is
    /// still captured even when the lambda's other configuration isn't understood.
    public ParseResult<(IReadOnlyList<TableConfig> Tables, IReadOnlyList<TemporalConfig> Temporal)> ParseTableMappings(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var tables = new List<TableConfig>();
        var temporal = new List<TemporalConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var toTableCall in FluentSyntaxHelpers.FindCallsNamed(scope, "ToTable"))
            {
                var arguments = toTableCall.ArgumentList.Arguments;

                if (arguments.Count == 0)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableToTableArgument,
                        "ToTable argument is not a string literal and could not be read.",
                        entityName,
                        PropertyName: null,
                        toTableCall.Span));
                    continue;
                }

                if (arguments.Count == 1 && arguments[0].Expression is AnonymousFunctionExpressionSyntax singleLambda)
                {
                    if (ContainsIsTemporalCall(singleLambda))
                    {
                        temporal.Add(new TemporalConfig(entityName));
                    }

                    continue;
                }

                if (arguments[0].Expression is not LiteralExpressionSyntax { } tableNameLiteral
                    || !tableNameLiteral.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableToTableArgument,
                        "ToTable argument is not a string literal and could not be read.",
                        entityName,
                        PropertyName: null,
                        toTableCall.Span));
                    continue;
                }

                string? schema = null;

                if (arguments.Count >= 2 && arguments[1].Expression is AnonymousFunctionExpressionSyntax pairedLambda)
                {
                    if (ContainsIsTemporalCall(pairedLambda))
                    {
                        temporal.Add(new TemporalConfig(entityName));
                    }
                }
                else if (arguments.Count >= 2)
                {
                    if (arguments[1].Expression is LiteralExpressionSyntax { } schemaLiteral
                        && schemaLiteral.IsKind(SyntaxKind.StringLiteralExpression))
                    {
                        schema = schemaLiteral.Token.ValueText;
                    }
                    else
                    {
                        diagnostics.Add(new Diagnostic(
                            DiagnosticCodes.UnreadableToTableArgument,
                            "ToTable schema argument is not a string literal and could not be read.",
                            entityName,
                            PropertyName: null,
                            toTableCall.Span));
                        continue;
                    }
                }

                tables.Add(new TableConfig(entityName, tableNameLiteral.Token.ValueText, schema));
            }
        }

        return new ParseResult<(IReadOnlyList<TableConfig>, IReadOnlyList<TemporalConfig>)>((tables, temporal), diagnostics);
    }

    private static bool ContainsIsTemporalCall(AnonymousFunctionExpressionSyntax lambda)
    {
        return lambda.DescendantNodesAndSelf()
            .OfType<InvocationExpressionSyntax>()
            .Any(invocation => invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "IsTemporal" });
    }
```

- [ ] **Step 6: Add the merge method**

In `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`, add after `ApplyJsonMappings`:

```csharp
    public static EntityModel ApplyTemporal(EntityModel entity, IReadOnlyList<TemporalConfig> configs)
    {
        return configs.Any(c => c.EntityName == entity.Name)
            ? entity with { IsTemporal = true }
            : entity;
    }
```

- [ ] **Step 7: Update `DiagramModelBuilder.Build`'s existing `tables` wiring**

In `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`, find:

```csharp
        var tables = configParser.ParseTableMappings(configSource);
```

(unchanged — the variable name stays, only its shape changed). Find:

```csharp
            .Select(entity => ModelMerger.ApplyTableMapping(entity, tables.Value))
```

and change to:

```csharp
            .Select(entity => ModelMerger.ApplyTableMapping(entity, tables.Value.Tables))
            .Select(entity => ModelMerger.ApplyTemporal(entity, tables.Value.Temporal))
```

(`diagnostics.AddRange(tables.Diagnostics);` is unchanged — `ParseResult<T>.Diagnostics` still exists regardless of `T`.)

- [ ] **Step 8: Run the full test suite**

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: PASS, all tests including the four new/updated `ParseTableMappings` tests, the `SetTable_UnknownEntity_InsertsNewEntityBlock` rewriter test, and both `RoundTripFuzzTests`.

- [ ] **Step 9: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Merging/TemporalConfig.cs src/EfSchemaVisualizer.Core/Model/EntityModel.cs src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs tests/EfSchemaVisualizer.Core.Tests/RoundTripFuzzTests.cs
git commit -m "Parse and merge temporal table config lambda (ToTable(b => b.IsTemporal()))"
```

---

### Task 8: `SplitToTable` — parse + merge

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Merging/SplitToTableConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Modify: `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`

**Interfaces:**
- Produces: `SplitToTableConfig(string EntityName, string TableName)`; `FluentConfigParser.ParseSplitTables(string sourceCode) : ParseResult<IReadOnlyList<SplitToTableConfig>>`; `ModelMerger.ApplySplitTables`; `EntityModel.SplitTables : IReadOnlyList<string>`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`:

```csharp
    private const string SplitToTableSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.SplitToTable("PersonDetails", tableBuilder =>
                    {
                        tableBuilder.Property(e => e.Bio);
                    });
                });
            }
        }
        """;

    [Fact]
    public void ParseSplitTables_ReadsSecondaryTableName()
    {
        var result = new FluentConfigParser().ParseSplitTables(SplitToTableSource);

        Assert.Empty(result.Diagnostics);
        var config = Assert.Single(result.Value);
        Assert.Equal("Person", config.EntityName);
        Assert.Equal("PersonDetails", config.TableName);
    }

    private const string SplitToTableSourceMultiple = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.SplitToTable("PersonDetails", tb => { tb.Property(e => e.Bio); })
                        .SplitToTable("PersonStats", tb => { tb.Property(e => e.LoginCount); });
                });
            }
        }
        """;

    [Fact]
    public void ParseSplitTables_MultipleCalls_ReadsAllSecondaryTableNames()
    {
        var result = new FluentConfigParser().ParseSplitTables(SplitToTableSourceMultiple);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(2, result.Value.Count);
        Assert.Contains(result.Value, c => c is { EntityName: "Person", TableName: "PersonDetails" });
        Assert.Contains(result.Value, c => c is { EntityName: "Person", TableName: "PersonStats" });
    }

    private const string SplitToTableSourceWithNonLiteralArg = """
        public class AppDbContext : DbContext
        {
            private const string TableName = "PersonDetails";

            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.SplitToTable(TableName, tb => { tb.Property(e => e.Bio); });
                });
            }
        }
        """;

    [Fact]
    public void ParseSplitTables_NonLiteralArgument_EmitsUnreadableSplitToTableArgumentDiagnostic()
    {
        var result = new FluentConfigParser().ParseSplitTables(SplitToTableSourceWithNonLiteralArg);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnreadableSplitToTableArgument, diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
    }
```

Append to `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`:

```csharp
    // ─── ApplySplitTables ──────────────────────────────────────────────────────────

    [Fact]
    public void ApplySplitTables_SetsSplitTablesFromMatchingConfigs_LeavesOtherEntitiesUntouched()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>());

        var merged = ModelMerger.ApplySplitTables(entity, new List<SplitToTableConfig>
        {
            new("Person", "PersonDetails"),
            new("Person", "PersonStats"),
            new("Address", "AddressExtra"),
        });

        Assert.Equal(new[] { "PersonDetails", "PersonStats" }, merged.SplitTables);
    }

    [Fact]
    public void ApplySplitTables_NoMatchingConfig_LeavesSplitTablesEmpty()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>());

        var merged = ModelMerger.ApplySplitTables(entity, new List<SplitToTableConfig> { new("Address", "AddressExtra") });

        Assert.Empty(merged.SplitTables);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~ParseSplitTables|FullyQualifiedName~ApplySplitTables"`
Expected: FAIL to compile.

- [ ] **Step 3: Create the DTO**

Create `src/EfSchemaVisualizer.Core/Merging/SplitToTableConfig.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Merging;

public sealed record SplitToTableConfig(string EntityName, string TableName);
```

- [ ] **Step 4: Add the model field**

In `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`, add `IReadOnlyList<string>? SplitTables = null` as the last positional parameter, after `bool IsTemporal = false`:

```csharp
    bool IsTemporal = false,
    IReadOnlyList<string>? SplitTables = null)
{
    public IReadOnlyList<string> KeyPropertyNames { get; init; } = KeyPropertyNames ?? new List<string>();
    public IReadOnlyList<IndexModel> Indexes { get; init; } = Indexes ?? new List<IndexModel>();
    public IReadOnlyList<IReadOnlyList<string>> AlternateKeys { get; init; } = AlternateKeys ?? new List<IReadOnlyList<string>>();
    public IReadOnlyList<string> SplitTables { get; init; } = SplitTables ?? new List<string>();
}
```

(This replaces the existing three-line `{ ... }` body — add the new `SplitTables` init line alongside the existing three.)

- [ ] **Step 5: Add the diagnostic code**

In `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`, add:

```csharp
    public const string UnreadableSplitToTableArgument = nameof(UnreadableSplitToTableArgument);
```

- [ ] **Step 6: Add the parser method**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, add `"SplitToTable"` to `RecognizedCallNames`, and add this method after `ParseJsonMappings`:

```csharp
    /// Only the secondary table name is read; the builder lambda's per-property table assignment
    /// is not modeled (same scope cut as `UsingEntity`'s join-config internals). `FindCallsNamed`
    /// finds every `SplitToTable` call in the scope regardless of how many are chained, so an
    /// entity split across three or more tables yields one config per call.
    public ParseResult<IReadOnlyList<SplitToTableConfig>> ParseSplitTables(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<SplitToTableConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "SplitToTable"))
            {
                var arg = call.ArgumentList.Arguments.FirstOrDefault();

                if (arg?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    results.Add(new SplitToTableConfig(entityName, literal.Token.ValueText));
                }
                else
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableSplitToTableArgument,
                        "SplitToTable table name argument is not a string literal and could not be read.",
                        entityName,
                        PropertyName: null,
                        (arg ?? (SyntaxNode)call).Span));
                }
            }
        }

        return new ParseResult<IReadOnlyList<SplitToTableConfig>>(results, diagnostics);
    }
```

- [ ] **Step 7: Add the merge method**

In `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`, add after `ApplyTemporal`:

```csharp
    public static EntityModel ApplySplitTables(EntityModel entity, IReadOnlyList<SplitToTableConfig> configs)
    {
        var tableNames = configs
            .Where(c => c.EntityName == entity.Name)
            .Select(c => c.TableName)
            .ToList();

        return tableNames.Count == 0 ? entity : entity with { SplitTables = tableNames };
    }
```

- [ ] **Step 8: Wire into `DiagramModelBuilder.Build`**

In `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`, add after the `jsonMappings` line:

```csharp
        var splitTables = configParser.ParseSplitTables(configSource);
```

Add after `diagnostics.AddRange(jsonMappings.Diagnostics);`:

```csharp
        diagnostics.AddRange(splitTables.Diagnostics);
```

Add after `.Select(entity => ModelMerger.ApplyJsonMappings(entity, jsonMappings.Value))` (before or after the `ApplyTemporal` line added in Task 7 — order between the two doesn't matter, they touch disjoint fields):

```csharp
            .Select(entity => ModelMerger.ApplySplitTables(entity, splitTables.Value))
```

- [ ] **Step 9: Run tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Merging/SplitToTableConfig.cs src/EfSchemaVisualizer.Core/Model/EntityModel.cs src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs
git commit -m "Parse and merge SplitToTable secondary table names"
```

---

### Task 9: `[InverseProperty]` — attribute parsing

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs`

**Interfaces:**
- Consumes: `EntityClassParser`'s existing private `FindAttribute`, `GetPositionalArg`, `TryReadStringArg` helpers (same file).
- Produces: `PropertyModel.InverseProperty : string?`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs` (near the existing `Parse_TimestampAttribute_SetsIsRowVersion` tests):

```csharp
    [Fact]
    public void Parse_InversePropertyAttribute_SetsInverseProperty()
    {
        const string source = """
            public class Blog
            {
                public int Id { get; set; }
                [InverseProperty("Blog")]
                public List<Post> Posts { get; set; } = new();
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var property = result.Value.Single().Properties.Single(p => p.Name == "Posts");
        Assert.Equal("Blog", property.InverseProperty);
    }

    [Fact]
    public void Parse_NoInversePropertyAttribute_LeavesInversePropertyNull()
    {
        const string source = """
            public class Blog
            {
                public int Id { get; set; }
                public List<Post> Posts { get; set; } = new();
            }
            """;

        var result = new EntityClassParser().Parse(source);

        var property = result.Value.Single().Properties.Single(p => p.Name == "Posts");
        Assert.Null(property.InverseProperty);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~Parse_InversePropertyAttribute|FullyQualifiedName~Parse_NoInversePropertyAttribute"`
Expected: FAIL to compile (`PropertyModel.InverseProperty` doesn't exist yet).

- [ ] **Step 3: Add the model field**

In `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`, add `string? InverseProperty = null` after `string? Collation = null`:

```csharp
    string? Collation = null,
    string? InverseProperty = null);
```

- [ ] **Step 4: Read the attribute**

In `src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs`, in `ParseProperty` (around line 219), add after the `isConcurrencyToken` line:

```csharp
        string? inverseProperty = FindAttribute(attributeLists, "InverseProperty") is { } inversePropertyAttr
            ? TryReadStringArg(GetPositionalArg(inversePropertyAttr, 0))
            : null;
```

Then add `InverseProperty: inverseProperty` to the `PropertyModel` constructor call at the end of the method:

```csharp
        return new PropertyModel(
            property.Identifier.Text,
            clrType,
            isNullable,
            maxLength,
            isRequiredOverride,
            precision,
            scale,
            columnName,
            columnType,
            IsRowVersion: isRowVersion,
            IsConcurrencyToken: isConcurrencyToken,
            InverseProperty: inverseProperty);
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Model/PropertyModel.cs src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs
git commit -m "Parse [InverseProperty] attribute into PropertyModel metadata"
```

---

### Task 10: Round-trip fuzz corpus extension

Confirms the eight new constructs land in `DiagramModelResult` correctly when run through the full pipeline together, and no longer trip the generic `UnrecognizedConfigCall` diagnostic.

**Files:**
- Modify: `tests/EfSchemaVisualizer.Core.Tests/RoundTripFuzzTests.cs`

**Interfaces:**
- Consumes: `FluentConfigParser.ParseUnrecognizedCalls(string) : IReadOnlyList<Diagnostic>` (existing).

- [ ] **Step 1: Write the failing test**

In `tests/EfSchemaVisualizer.Core.Tests/RoundTripFuzzTests.cs`, add a new private const source and test, after the existing `ConfigSource` field and before `UnsupportedHasDefaultValueSql_IsNotReadIntoTheModel`:

```csharp
    private const string PriorityThreeConfigSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Blog>(entity =>
                {
                    entity.HasQueryFilter(e => e.Url != null);
                    entity.SplitToTable("BlogStats", tb => { tb.Property(e => e.Rating); });
                    entity.ToTable(b => b.IsTemporal());
                });

                modelBuilder.Entity<Post>(entity =>
                {
                    entity.Property(e => e.Title).HasMaxLength(200);
                });
            }
        }
        """;

    [Fact]
    public void PriorityThreeConstructs_AreReadIntoTheModel_NotFlaggedAsUnrecognized()
    {
        var parser = new FluentConfigParser();

        var queryFilters = parser.ParseQueryFilters(PriorityThreeConfigSource).Value;
        Assert.Contains(queryFilters, c => c.EntityName == "Blog");

        var splitTables = parser.ParseSplitTables(PriorityThreeConfigSource).Value;
        Assert.Contains(splitTables, c => c is { EntityName: "Blog", TableName: "BlogStats" });

        var tables = parser.ParseTableMappings(PriorityThreeConfigSource).Value;
        Assert.Contains(tables.Temporal, c => c.EntityName == "Blog");

        var unrecognized = parser.ParseUnrecognizedCalls(PriorityThreeConfigSource);
        Assert.Empty(unrecognized);
    }

    [Fact]
    public void RenamingUnrelatedEntitysProperty_LeavesPriorityThreeConstructsOnBlogUntouched()
    {
        var modelRewriter = new OnModelCreatingRewriter();

        // Rename a property on Post — a different entity from the one carrying the
        // HasQueryFilter/SplitToTable/temporal config — and confirm Blog's Priority 3
        // constructs still parse out of the regenerated source afterward.
        var renamedConfigSource = modelRewriter.RenamePropertyReferences(
            PriorityThreeConfigSource, "Post", "Title", "Headline");

        Assert.Contains("entity.Property(e => e.Headline)", renamedConfigSource);

        var parser = new FluentConfigParser();

        var queryFilters = parser.ParseQueryFilters(renamedConfigSource).Value;
        Assert.Contains(queryFilters, c => c.EntityName == "Blog");

        var splitTables = parser.ParseSplitTables(renamedConfigSource).Value;
        Assert.Contains(splitTables, c => c is { EntityName: "Blog", TableName: "BlogStats" });

        var tables = parser.ParseTableMappings(renamedConfigSource).Value;
        Assert.Contains(tables.Temporal, c => c.EntityName == "Blog");
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test EfSchemaVisualizer.slnx --filter "FullyQualifiedName~PriorityThreeConstructs|FullyQualifiedName~RenamingUnrelatedEntitysProperty_LeavesPriorityThree"`
Expected: FAIL — at this point in the plan every referenced method already exists (this is the last task), so this should actually compile; if it fails, it indicates a wiring gap from an earlier task (e.g. `RecognizedCallNames` missing an entry) rather than a missing method. Investigate before proceeding rather than assuming this step is a normal "red" step.

- [ ] **Step 3: Fix any gap found**

If `ParseUnrecognizedCalls` still flags `HasQueryFilter`, `SplitToTable`, or `ToTable` on the temporal path, check `RecognizedCallNames` in `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs` for the missing entry (`"ToTable"` should already have been present before this plan started; `"HasQueryFilter"` and `"SplitToTable"` were added in Tasks 1 and 8 respectively) and add it.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test EfSchemaVisualizer.slnx`
Expected: PASS — full suite green.

- [ ] **Step 5: Commit**

```bash
git add tests/EfSchemaVisualizer.Core.Tests/RoundTripFuzzTests.cs
git commit -m "Extend round-trip fuzz corpus with Priority 3 constructs"
```

---

## Post-plan: update the backlog

After all ten tasks are committed, edit `docs/backlog.md`'s Priority 3 "Smaller unread config" line (currently `- [ ]`) to `- [x]` with an `**Update:**` paragraph summarizing what was parsed, following the exact style of every other closed item in that file (e.g. the `Concurrency tokens unread` entry immediately above it). This is a documentation-only change, not a code task — do it by hand after verifying the full test suite is green, not as part of any task above.
