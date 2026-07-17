# Ignore, [Index] attribute, Value Generation, Shadow Properties Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Parse and surface four previously-dropped EF Core constructs — `Ignore`/`Ignore<T>()`, the `[Index]` class attribute, value-generation calls (`ValueGeneratedOnAdd`/`OnUpdate`/`Never`, `UseIdentityColumn`), and shadow properties (`Property<T>("Name")`) — so they stop silently disappearing from the rendered diagram.

**Architecture:** Every addition follows the existing parse → `Merging/*Config` DTO → `ModelMerger.Apply*` → `DiagramModelBuilder.Build` pipeline already used by every other fluent-config feature in this codebase. No rewriter/write-back work is in scope — these are read-only diagram improvements (parse + merge + UI display only), matching how prior annotation-support and relationship-parsing slices landed.

**Tech Stack:** C#/.NET, Roslyn (`Microsoft.CodeAnalysis.CSharp`) for syntax-only parsing, xUnit for tests, Blazor `.razor` components for the diagram UI.

## Global Constraints

- No rewriter/write-back support for any of the four features (no `SetIgnore`, `SetValueGeneration`, attribute-driven `SetIndex`, or shadow-property creation/edit in `DiagramEditor`). These are parse+merge+display only.
- `modelBuilder.Ignore<T>()` is only recognized as a direct, unconditional top-level statement shape — no support for it appearing conditionally or inside a loop, consistent with every other parser in this codebase being syntax-only and non-executing.
- Value generation UI is a **read-only badge only** — no editor control.
- Shadow property rows in the diagram are **read-only** — no rename/retype/remove/expand-panel affordances, since there's no rewriter support to back edits.
- Every new parser method follows the existing `ParseResult<T>` / `Diagnostic` shape already used throughout `FluentConfigParser` and `EntityClassParser`.
- All new `Merging/*Config` DTOs are `sealed record` types living in `EfSchemaVisualizer.Core.Merging`, matching every existing config DTO in that folder.
- 401/401 existing tests must stay green throughout; every task adds new tests and must leave the full suite passing before it's considered done.

---

### Task 1: Shared property-name-argument helper + `Ignore` (property-level)

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Create: `src/EfSchemaVisualizer.Core/Merging/IgnoreConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.FindConfigurationScopes(CompilationUnitSyntax root)` → `IEnumerable<(string EntityName, SyntaxNode Scope)>`; `FluentSyntaxHelpers.FindCallsNamed(SyntaxNode scope, string methodName)` → `IEnumerable<InvocationExpressionSyntax>` (both already exist, unchanged).
- Produces: `FluentSyntaxHelpers.TryReadSinglePropertyNameArgument(InvocationExpressionSyntax call)` → `string?` (new, `internal`). `IgnoreConfig(string EntityName, string PropertyName)` record. `FluentConfigParser.ParseIgnoredProperties(string sourceCode)` → `ParseResult<IReadOnlyList<IgnoreConfig>>`. `ModelMerger.ApplyIgnoredProperties(EntityModel entity, IReadOnlyList<IgnoreConfig> configs)` → `EntityModel`. `DiagnosticCodes.UnreadableIgnoreArgument`. These are consumed by Task 2 (whole-entity `Ignore`, same file) and by `DiagramModelBuilder.Build`.

- [ ] **Step 1: Refactor `GetPropertyNameForPropertyCall` to extract a shared, `Ignore`-reusable argument-resolution helper**

  In `src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs`, replace the existing `GetPropertyNameForPropertyCall` method (lines 193–213) with:

  ```csharp
    /// Given a bare `entity.Property(e => e.Name)` invocation itself (string overload and
    /// block-bodied lambda also resolved), returns "Name" without requiring a `.HasMaxLength(...)`
    /// (or any other) call chained onto it.
    public static string? GetPropertyNameForPropertyCall(InvocationExpressionSyntax propertyInvocation)
    {
        if (propertyInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Property" })
        {
            return null;
        }

        return TryReadSinglePropertyNameArgument(propertyInvocation);
    }

    /// Resolves a fluent call's first argument to a property name: `e => e.Name` (expression- or
    /// single-return-block-bodied, `Simple`/`Parenthesized` lambda), or a string literal `"Name"`.
    /// Shared by `Property(...)`-name resolution above and any other single-argument fluent call
    /// keyed by property (e.g. `Ignore(e => e.X)` / `Ignore("X")`).
    internal static string? TryReadSinglePropertyNameArgument(InvocationExpressionSyntax call)
    {
        var argumentExpression = call.ArgumentList.Arguments
            .Select(a => a.Expression)
            .FirstOrDefault();

        return argumentExpression switch
        {
            SimpleLambdaExpressionSyntax { ExpressionBody: MemberAccessExpressionSyntax { Name.Identifier.Text: var name } } => name,
            SimpleLambdaExpressionSyntax { Block: { Statements: [ReturnStatementSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: var name } }] } } => name,
            ParenthesizedLambdaExpressionSyntax { ExpressionBody: MemberAccessExpressionSyntax { Name.Identifier.Text: var name } } => name,
            ParenthesizedLambdaExpressionSyntax { Block: { Statements: [ReturnStatementSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: var name } }] } } => name,
            LiteralExpressionSyntax { Token.ValueText: var text } literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => text,
            _ => null,
        };
    }
  ```

  This is a pure refactor (no behavior change) — `GetPropertyNameForPropertyCall`'s existing callers and tests must keep passing unchanged.

- [ ] **Step 2: Run existing tests to confirm the refactor is behavior-preserving**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~FluentSyntaxHelpersTests`
  Expected: all existing tests PASS (no behavior change).

- [ ] **Step 3: Add `DiagnosticCodes.UnreadableIgnoreArgument`**

  In `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`, add a new line after `UnreadableHasIndexArgument`:

  ```csharp
    public const string UnreadableIgnoreArgument = nameof(UnreadableIgnoreArgument);
  ```

- [ ] **Step 4: Create the `IgnoreConfig` DTO**

  Create `src/EfSchemaVisualizer.Core/Merging/IgnoreConfig.cs`:

  ```csharp
  namespace EfSchemaVisualizer.Core.Merging;

  public sealed record IgnoreConfig(string EntityName, string PropertyName);
  ```

- [ ] **Step 5: Write failing tests for `FluentConfigParser.ParseIgnoredProperties`**

  In `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`, add a new region at the end of the class (before the closing `}`):

  ```csharp
    // ─── ParseIgnoredProperties ────────────────────────────────────────────────────

    [Fact]
    public void ParseIgnoredProperties_LambdaForm_ReadsPropertyName()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.Ignore(e => e.Notes);
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseIgnoredProperties(source);

        var config = Assert.Single(result.Value);
        Assert.Equal("Person", config.EntityName);
        Assert.Equal("Notes", config.PropertyName);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseIgnoredProperties_StringOverload_ReadsPropertyName()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.Ignore("Notes");
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseIgnoredProperties(source);

        var config = Assert.Single(result.Value);
        Assert.Equal("Person", config.EntityName);
        Assert.Equal("Notes", config.PropertyName);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseIgnoredProperties_UnresolvableArgument_EmitsDiagnostic()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.Ignore(GetIgnoredPropertyName());
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseIgnoredProperties(source);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnreadableIgnoreArgument, diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
    }

    [Fact]
    public void ParseIgnoredProperties_IEntityTypeConfigurationStyle_ReadsPropertyName()
    {
        const string source = """
            class PersonConfig : IEntityTypeConfiguration<Person> {
                public void Configure(EntityTypeBuilder<Person> builder) {
                    builder.Ignore(e => e.Notes);
                }
            }
            """;

        var result = new FluentConfigParser().ParseIgnoredProperties(source);

        var config = Assert.Single(result.Value);
        Assert.Equal("Person", config.EntityName);
        Assert.Equal("Notes", config.PropertyName);
    }
  ```

- [ ] **Step 6: Run the new tests to verify they fail**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~ParseIgnoredProperties`
  Expected: FAIL with "ParseIgnoredProperties" not found on `FluentConfigParser` (compile error).

- [ ] **Step 7: Implement `FluentConfigParser.ParseIgnoredProperties`**

  In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, add `"Ignore"` to the `RecognizedCallNames` set (line 16-21):

  ```csharp
    private static readonly HashSet<string> RecognizedCallNames = new()
    {
        "Property", "HasMaxLength", "HasPrecision", "IsRequired", "HasKey", "ToTable",
        "HasColumnName", "HasColumnType", "HasDefaultValue", "HasIndex", "IsUnique",
        "HasOne", "HasMany", "WithOne", "WithMany", "HasForeignKey", "OnDelete", "UsingEntity",
        "Ignore", "ValueGeneratedOnAdd", "ValueGeneratedOnUpdate", "ValueGeneratedOnAddOrUpdate",
        "ValueGeneratedNever", "UseIdentityColumn",
    };
  ```

  (The five value-generation names are added now so Task 4 doesn't need to touch this set again — they're inert until Task 4 adds the parser method that reads them.)

  Then add this method anywhere among the other `Parse*` methods (e.g. after `ParseIndexes`):

  ```csharp
    public ParseResult<IReadOnlyList<IgnoreConfig>> ParseIgnoredProperties(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<IgnoreConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "Ignore"))
            {
                var propertyName = FluentSyntaxHelpers.TryReadSinglePropertyNameArgument(call);

                if (propertyName is null)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableIgnoreArgument,
                        "Ignore argument could not be read as a property name.",
                        entityName,
                        PropertyName: null,
                        call.Span));
                    continue;
                }

                results.Add(new IgnoreConfig(entityName, propertyName));
            }
        }

        return new ParseResult<IReadOnlyList<IgnoreConfig>>(results, diagnostics);
    }
  ```

  Add `using EfSchemaVisualizer.Core.Merging;` at the top if not already present (it already is, per the existing `using` block).

- [ ] **Step 8: Run the new tests to verify they pass**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~ParseIgnoredProperties`
  Expected: PASS (4 tests).

- [ ] **Step 9: Write a failing test for `ModelMerger.ApplyIgnoredProperties`**

  In `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`, add:

  ```csharp
    // ─── ApplyIgnoredProperties ────────────────────────────────────────────────────

    [Fact]
    public void ApplyIgnoredProperties_RemovesMatchingProperty_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Notes", "string", IsNullable: true, MaxLength: null),
        });

        var configs = new List<IgnoreConfig>
        {
            new("Person", "Notes"),
            new("Address", "Line1"), // different entity, must not affect Person
        };

        var merged = ModelMerger.ApplyIgnoredProperties(entity, configs);

        Assert.Single(merged.Properties);
        Assert.Equal("Id", merged.Properties[0].Name);
    }

    [Fact]
    public void ApplyIgnoredProperties_NoMatchingConfig_ReturnsEntityUnchanged()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
        });

        var merged = ModelMerger.ApplyIgnoredProperties(entity, new List<IgnoreConfig>());

        Assert.Single(merged.Properties);
    }
  ```

- [ ] **Step 10: Run test to verify it fails**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~ApplyIgnoredProperties`
  Expected: FAIL with "ApplyIgnoredProperties" not found on `ModelMerger` (compile error).

- [ ] **Step 11: Implement `ModelMerger.ApplyIgnoredProperties`**

  In `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`, add (e.g. after `ApplyDefaultValues`):

  ```csharp
    public static EntityModel ApplyIgnoredProperties(EntityModel entity, IReadOnlyList<IgnoreConfig> configs)
    {
        var ignoredNames = configs
            .Where(c => c.EntityName == entity.Name)
            .Select(c => c.PropertyName)
            .ToHashSet();

        if (ignoredNames.Count == 0)
        {
            return entity;
        }

        var updatedProperties = entity.Properties
            .Where(property => !ignoredNames.Contains(property.Name))
            .ToList();

        return entity with { Properties = updatedProperties };
    }
  ```

- [ ] **Step 12: Run test to verify it passes**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~ApplyIgnoredProperties`
  Expected: PASS (2 tests).

- [ ] **Step 13: Wire `ParseIgnoredProperties`/`ApplyIgnoredProperties` into `DiagramModelBuilder.Build`**

  In `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`, add this parse call alongside the others (after `var unrecognizedCalls = ...`):

  ```csharp
        var ignoredProperties = configParser.ParseIgnoredProperties(configSource);
  ```

  Add its diagnostics to the list (after `diagnostics.AddRange(unrecognizedCalls);`):

  ```csharp
        diagnostics.AddRange(ignoredProperties.Diagnostics);
  ```

  Add `.Select(entity => ModelMerger.ApplyIgnoredProperties(entity, ignoredProperties.Value))` as the last `.Select(...)` in the `entities` pipeline (after `ApplyIndexes`):

  ```csharp
        var entities = entityResult.Value
            .Select(entity => ModelMerger.ApplyMaxLengths(entity, maxLengths.Value))
            .Select(entity => ModelMerger.ApplyPrecisions(entity, precisions.Value))
            .Select(entity => ModelMerger.ApplyIsRequired(entity, isRequired.Value))
            .Select(entity => ModelMerger.ApplyKeys(entity, keys.Value))
            .Select(entity => ModelMerger.ApplyTableMapping(entity, tables.Value))
            .Select(entity => ModelMerger.ApplyColumnNames(entity, columnNames.Value))
            .Select(entity => ModelMerger.ApplyColumnTypes(entity, columnTypes.Value))
            .Select(entity => ModelMerger.ApplyDefaultValues(entity, defaultValues.Value))
            .Select(entity => ModelMerger.ApplyIndexes(entity, indexes.Value))
            .Select(entity => ModelMerger.ApplyIgnoredProperties(entity, ignoredProperties.Value))
            .ToList();
  ```

- [ ] **Step 14: Write a failing end-to-end test in `DiagramModelBuilderTests`**

  In `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs`, add:

  ```csharp
    [Fact]
    public void Build_IgnoredProperty_IsDroppedFromDiagram()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }
                public string Notes { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.Ignore(e => e.Notes);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var person = result.Entities.Single();
        Assert.DoesNotContain(person.Properties, p => p.Name == "Notes");
        Assert.Contains(person.Properties, p => p.Name == "Id");
    }
  ```

- [ ] **Step 15: Run test to verify it fails, then implement, then verify it passes**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~Build_IgnoredProperty_IsDroppedFromDiagram`
  Expected before Step 13's wiring: FAIL (property still present). After Step 13 is done (it already will be, since this step comes after): PASS.

  Since Step 13 already wired the code, this test should PASS on first run. Confirm: PASS.

- [ ] **Step 16: Run the full test suite**

  Run: `dotnet test EfSchemaVisualizer.slnx`
  Expected: all tests PASS (previous count + 7 new tests).

- [ ] **Step 17: Commit**

  ```bash
  git add src/EfSchemaVisualizer.Core/Parsing/FluentSyntaxHelpers.cs \
          src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs \
          src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs \
          src/EfSchemaVisualizer.Core/Merging/IgnoreConfig.cs \
          src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs \
          src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs \
          tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs \
          tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs \
          tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs
  git commit -m "Parse and merge property-level Ignore, drop ignored properties from diagram"
  ```

---

### Task 2: `Ignore` (whole-entity) — `modelBuilder.Ignore<T>()`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs`

**Interfaces:**
- Consumes: nothing new (works directly off `CSharpSyntaxTree.ParseText`).
- Produces: `FluentConfigParser.ParseIgnoredEntities(string sourceCode)` → `IReadOnlyList<string>` (entity type names). Consumed by `DiagramModelBuilder.Build` to filter `entityResult.Value`, `mergedRelationshipConfigs`, and (transitively) every `Apply*` call already added in Task 1.

- [ ] **Step 1: Write failing tests for `ParseIgnoredEntities`**

  In `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`, add:

  ```csharp
    // ─── ParseIgnoredEntities ──────────────────────────────────────────────────────

    [Fact]
    public void ParseIgnoredEntities_BareGenericCall_ReadsEntityTypeName()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Ignore<AuditLog>();
                }
            }
            """;

        var result = new FluentConfigParser().ParseIgnoredEntities(source);

        Assert.Equal(new[] { "AuditLog" }, result);
    }

    [Fact]
    public void ParseIgnoredEntities_NoIgnoreCalls_ReturnsEmpty()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => { });
                }
            }
            """;

        var result = new FluentConfigParser().ParseIgnoredEntities(source);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseIgnoredEntities_DoesNotConfusePropertyLevelIgnoreWithWholeEntityIgnore()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.Ignore(e => e.Notes);
                    });
                    modelBuilder.Ignore<AuditLog>();
                }
            }
            """;

        var result = new FluentConfigParser().ParseIgnoredEntities(source);

        Assert.Equal(new[] { "AuditLog" }, result);
    }
  ```

- [ ] **Step 2: Run tests to verify they fail**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~ParseIgnoredEntities`
  Expected: FAIL with "ParseIgnoredEntities" not found on `FluentConfigParser` (compile error).

- [ ] **Step 3: Implement `ParseIgnoredEntities`**

  In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, add (after `ParseIgnoredProperties`):

  ```csharp
    /// Reads bare `modelBuilder.Ignore<T>()` calls (whole-entity ignore). Distinguished from the
    /// property-level `entity.Ignore(e => e.X)` / `entity.Ignore("X")` overloads by shape alone:
    /// this one is always generic with zero arguments, the property-level one is always
    /// non-generic with exactly one argument, so no scope/receiver disambiguation is needed.
    public IReadOnlyList<string> ParseIgnoredEntities(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<string>();

        foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax
                {
                    Name: GenericNameSyntax { Identifier.Text: "Ignore", TypeArgumentList.Arguments: [var typeArg] },
                })
            {
                continue;
            }

            if (invocation.ArgumentList.Arguments.Count != 0)
            {
                continue;
            }

            results.Add(typeArg.ToString());
        }

        return results.Distinct().ToList();
    }
  ```

- [ ] **Step 4: Run tests to verify they pass**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~ParseIgnoredEntities`
  Expected: PASS (3 tests).

- [ ] **Step 5: Write failing end-to-end tests for whole-entity ignore in the diagram**

  In `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs`, add:

  ```csharp
    [Fact]
    public void Build_IgnoredEntity_IsDroppedFromDiagram()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }
            }

            public class AuditLog
            {
                public int Id { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Ignore<AuditLog>();
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.DoesNotContain(result.Entities, e => e.Name == "AuditLog");
        Assert.Contains(result.Entities, e => e.Name == "Person");
    }

    [Fact]
    public void Build_IgnoredEntity_DropsRelationshipsReferencingIt()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }
                public List<AuditLog> Logs { get; set; }
            }

            public class AuditLog
            {
                public int Id { get; set; }
                public int PersonId { get; set; }
                public Person Person { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.HasMany(p => p.Logs).WithOne(a => a.Person).HasForeignKey(a => a.PersonId);
                    });
                    modelBuilder.Ignore<AuditLog>();
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.Empty(result.Relationships);
    }
  ```

- [ ] **Step 6: Run tests to verify they fail**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~Build_IgnoredEntity`
  Expected: FAIL (both entities still present; relationship still present).

- [ ] **Step 7: Wire whole-entity ignore filtering into `DiagramModelBuilder.Build`**

  In `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`, add a call right after `var ignoredProperties = configParser.ParseIgnoredProperties(configSource);`:

  ```csharp
        var ignoredEntityNames = configParser.ParseIgnoredEntities(configSource).ToHashSet();
  ```

  Change the `entities` pipeline's source from `entityResult.Value` to filter out ignored entities first:

  ```csharp
        var entities = entityResult.Value
            .Where(entity => !ignoredEntityNames.Contains(entity.Name))
            .Select(entity => ModelMerger.ApplyMaxLengths(entity, maxLengths.Value))
            .Select(entity => ModelMerger.ApplyPrecisions(entity, precisions.Value))
            .Select(entity => ModelMerger.ApplyIsRequired(entity, isRequired.Value))
            .Select(entity => ModelMerger.ApplyKeys(entity, keys.Value))
            .Select(entity => ModelMerger.ApplyTableMapping(entity, tables.Value))
            .Select(entity => ModelMerger.ApplyColumnNames(entity, columnNames.Value))
            .Select(entity => ModelMerger.ApplyColumnTypes(entity, columnTypes.Value))
            .Select(entity => ModelMerger.ApplyDefaultValues(entity, defaultValues.Value))
            .Select(entity => ModelMerger.ApplyIndexes(entity, indexes.Value))
            .Select(entity => ModelMerger.ApplyIgnoredProperties(entity, ignoredProperties.Value))
            .ToList();
  ```

  Change the `mergedRelationshipConfigs` assembly to also drop relationships touching an ignored entity:

  ```csharp
        var mergedRelationshipConfigs = fluentRelationships.Value
            .Concat(annotationRelationships.Value.Where(r => !fluentRelationshipKeys.Contains(RelationshipDedupeKey(r))))
            .Where(r => !ignoredEntityNames.Contains(r.PrincipalEntity) && !ignoredEntityNames.Contains(r.DependentEntity))
            .ToList();
  ```

  Add `using System.Linq;` at the top of the file if not already present (check first — it likely already is, since `.Select`/`.Concat` are already used).

- [ ] **Step 8: Run tests to verify they pass**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~Build_IgnoredEntity`
  Expected: PASS (2 tests).

- [ ] **Step 9: Run the full test suite**

  Run: `dotnet test EfSchemaVisualizer.slnx`
  Expected: all tests PASS (previous count + 5 new tests).

- [ ] **Step 10: Commit**

  ```bash
  git add src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs \
          src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs \
          tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs \
          tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs
  git commit -m "Parse whole-entity modelBuilder.Ignore<T>() and drop it (and its relationships) from the diagram"
  ```

---

### Task 3: `[Index]` class-level attribute

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs`
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs`

**Interfaces:**
- Consumes: `IndexConfig(string EntityName, IReadOnlyList<string> PropertyNames, bool IsUnique, string? Name)` (existing, from `Merging/IndexConfig.cs`). `ModelMerger.ApplyIndexes(EntityModel entity, IReadOnlyList<IndexConfig> configs)` (existing, unchanged signature).
- Produces: `EntityClassParser.ParseIndexAttributes(string sourceCode)` → `ParseResult<IReadOnlyList<IndexConfig>>`. Consumed by `DiagramModelBuilder.Build`, unioned with the existing fluent `HasIndex` results before calling `ModelMerger.ApplyIndexes`.

- [ ] **Step 1: Write failing tests for `[Index]` attribute parsing**

  In `tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs`, add at the end of the class:

  ```csharp
    // ─── ParseIndexAttributes ──────────────────────────────────────────────────────

    [Fact]
    public void ParseIndexAttributes_SinglePropertyViaNameof_ReadsPropertyName()
    {
        const string source = """
            [Index(nameof(Email))]
            public class Person
            {
                public int Id { get; set; }
                public string Email { get; set; }
            }
            """;

        var result = new EntityClassParser().ParseIndexAttributes(source);

        var config = Assert.Single(result.Value);
        Assert.Equal("Person", config.EntityName);
        Assert.Equal(new[] { "Email" }, config.PropertyNames);
        Assert.False(config.IsUnique);
        Assert.Null(config.Name);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseIndexAttributes_BareStringLiteral_ReadsPropertyName()
    {
        const string source = """
            [Index("Email")]
            public class Person
            {
                public int Id { get; set; }
                public string Email { get; set; }
            }
            """;

        var result = new EntityClassParser().ParseIndexAttributes(source);

        var config = Assert.Single(result.Value);
        Assert.Equal(new[] { "Email" }, config.PropertyNames);
    }

    [Fact]
    public void ParseIndexAttributes_CompositeProperties_PreservesOrder()
    {
        const string source = """
            [Index(nameof(LastName), nameof(FirstName))]
            public class Person
            {
                public int Id { get; set; }
                public string FirstName { get; set; }
                public string LastName { get; set; }
            }
            """;

        var result = new EntityClassParser().ParseIndexAttributes(source);

        var config = Assert.Single(result.Value);
        Assert.Equal(new[] { "LastName", "FirstName" }, config.PropertyNames);
    }

    [Fact]
    public void ParseIndexAttributes_NamedArgsIsUniqueAndName_AreRead()
    {
        const string source = """
            [Index(nameof(Email), IsUnique = true, Name = "IX_Person_Email")]
            public class Person
            {
                public int Id { get; set; }
                public string Email { get; set; }
            }
            """;

        var result = new EntityClassParser().ParseIndexAttributes(source);

        var config = Assert.Single(result.Value);
        Assert.True(config.IsUnique);
        Assert.Equal("IX_Person_Email", config.Name);
    }

    [Fact]
    public void ParseIndexAttributes_MultipleAttributesOnSameClass_AllRead()
    {
        const string source = """
            [Index(nameof(Email))]
            [Index(nameof(LastName))]
            public class Person
            {
                public int Id { get; set; }
                public string Email { get; set; }
                public string LastName { get; set; }
            }
            """;

        var result = new EntityClassParser().ParseIndexAttributes(source);

        Assert.Equal(2, result.Value.Count);
        Assert.Contains(result.Value, c => c.PropertyNames.SequenceEqual(new[] { "Email" }));
        Assert.Contains(result.Value, c => c.PropertyNames.SequenceEqual(new[] { "LastName" }));
    }

    [Fact]
    public void ParseIndexAttributes_NoIndexAttribute_ReturnsEmpty()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
            }
            """;

        var result = new EntityClassParser().ParseIndexAttributes(source);

        Assert.Empty(result.Value);
        Assert.Empty(result.Diagnostics);
    }
  ```

  Ensure `using System.Linq;` is present at the top of this test file (it already is, since other tests use LINQ — verify before assuming).

- [ ] **Step 2: Run tests to verify they fail**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~ParseIndexAttributes`
  Expected: FAIL with "ParseIndexAttributes" not found on `EntityClassParser` (compile error).

- [ ] **Step 3: Implement `ParseIndexAttributes`**

  In `src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs`, add these methods (e.g. after `ParseTableAttribute`):

  ```csharp
    public ParseResult<IReadOnlyList<IndexConfig>> ParseIndexAttributes(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var typeDeclarations = root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t is ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax)
            .Where(t => !t.Ancestors().OfType<TypeDeclarationSyntax>().Any());

        var results = new List<IndexConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var typeDeclaration in typeDeclarations)
        {
            var entityName = typeDeclaration.Identifier.Text;

            foreach (var indexAttr in FindAttributes(typeDeclaration.AttributeLists, "Index"))
            {
                var positionalArgs = indexAttr.ArgumentList?.Arguments
                    .Where(a => a.NameEquals is null)
                    .ToList() ?? new List<AttributeArgumentSyntax>();

                if (positionalArgs.Count == 0)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableHasIndexArgument,
                        "[Index] attribute has no property name arguments.",
                        entityName,
                        PropertyName: null,
                        indexAttr.Span));
                    continue;
                }

                var propertyNames = new List<string>();
                var unresolved = false;

                foreach (var arg in positionalArgs)
                {
                    var name = TryReadIndexAttributePropertyName(arg);
                    if (name is null)
                    {
                        unresolved = true;
                        break;
                    }

                    propertyNames.Add(name);
                }

                if (unresolved)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableHasIndexArgument,
                        "[Index] attribute argument(s) could not be read as property name(s).",
                        entityName,
                        PropertyName: null,
                        indexAttr.Span));
                    continue;
                }

                var isUnique = TryReadBoolArg(GetNamedArg(indexAttr, "IsUnique"));
                var name = TryReadStringArg(GetNamedArg(indexAttr, "Name"));

                results.Add(new IndexConfig(entityName, propertyNames, isUnique, name));
            }
        }

        return new ParseResult<IReadOnlyList<IndexConfig>>(results, diagnostics);
    }

    private static IEnumerable<AttributeSyntax> FindAttributes(SyntaxList<AttributeListSyntax> attributeLists, string simpleName)
    {
        return attributeLists
            .SelectMany(list => list.Attributes)
            .Where(attribute => attribute.Name.ToString() is var name
                && (name == simpleName || name == simpleName + "Attribute"));
    }

    private static string? TryReadIndexAttributePropertyName(AttributeArgumentSyntax arg)
    {
        return arg.Expression switch
        {
            LiteralExpressionSyntax literal when literal.IsKind(SyntaxKind.StringLiteralExpression) => literal.Token.ValueText,
            InvocationExpressionSyntax
            {
                Expression: IdentifierNameSyntax { Identifier.Text: "nameof" },
                ArgumentList.Arguments: [var nameofArg],
            } => nameofArg.Expression switch
            {
                IdentifierNameSyntax id => id.Identifier.Text,
                MemberAccessExpressionSyntax { Name.Identifier.Text: var memberName } => memberName,
                _ => null,
            },
            _ => null,
        };
    }

    private static bool TryReadBoolArg(AttributeArgumentSyntax? arg)
    {
        return arg?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.TrueLiteralExpression);
    }
  ```

  This reuses the existing private `GetNamedArg` and `TryReadStringArg` helpers already defined lower in this file (lines 242–253) — no changes needed to them.

  Add `using EfSchemaVisualizer.Core.Merging;` at the top if not already present (it already is, per the existing `using` block — `RelationshipConfig` from that namespace is already referenced).

- [ ] **Step 4: Run tests to verify they pass**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~ParseIndexAttributes`
  Expected: PASS (6 tests).

- [ ] **Step 5: Write a failing end-to-end test for fluent-wins conflict resolution**

  In `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs`, add:

  ```csharp
    [Fact]
    public void Build_IndexAttributeOnly_NoFluentConfig_AttributeIndexUsed()
    {
        const string classSource = """
            [Index(nameof(Email))]
            public class Person
            {
                public int Id { get; set; }
                public string Email { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var index = Assert.Single(result.Entities.Single().Indexes);
        Assert.Equal(new[] { "Email" }, index.PropertyNames);
    }

    [Fact]
    public void Build_FluentIndexAndAttributeIndexOnSameProperties_FluentWins()
    {
        const string classSource = """
            [Index(nameof(Email))]
            public class Person
            {
                public int Id { get; set; }
                public string Email { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.HasIndex(e => e.Email).IsUnique();
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var index = Assert.Single(result.Entities.Single().Indexes);
        Assert.True(index.IsUnique);
    }

    [Fact]
    public void Build_FluentIndexAndAttributeIndexOnDifferentProperties_BothPresent()
    {
        const string classSource = """
            [Index(nameof(LastName))]
            public class Person
            {
                public int Id { get; set; }
                public string Email { get; set; }
                public string LastName { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.HasIndex(e => e.Email);
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        Assert.Equal(2, result.Entities.Single().Indexes.Count);
    }
  ```

- [ ] **Step 6: Run tests to verify they fail**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~Build_IndexAttribute\|Build_FluentIndexAndAttribute`
  Expected: FAIL (`Build_IndexAttributeOnly_NoFluentConfig_AttributeIndexUsed` fails with "Assert.Single() Collection is empty"; the fluent-wins test fails because there's no conflict-resolution wiring yet — actually with current code the attribute index simply wouldn't exist, so `[Index]` is invisible and only the fluent index shows, meaning `Build_FluentIndexAndAttributeIndexOnSameProperties_FluentWins` would spuriously pass already — that's fine, it will still assert the right behavior after wiring. `Build_FluentIndexAndAttributeIndexOnDifferentProperties_BothPresent` FAILs, expecting 2 but finding 1.)

- [ ] **Step 7: Wire `ParseIndexAttributes` into `DiagramModelBuilder.Build` with fluent-wins dedup**

  In `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`, add the parse call alongside the other annotation-derived calls (after `var annotationRelationships = entityParser.ParseRelationships(classSource, entityResult.Value);`):

  ```csharp
        var indexAttributes = entityParser.ParseIndexAttributes(classSource);
  ```

  Add its diagnostics (near the other `diagnostics.AddRange(...)` calls):

  ```csharp
        diagnostics.AddRange(indexAttributes.Diagnostics);
  ```

  Before the `entities` pipeline, compute the merged, deduped index-config list:

  ```csharp
        var fluentIndexKeys = indexes.Value.Select(IndexDedupeKey).ToHashSet();
        var mergedIndexConfigs = indexAttributes.Value
            .Where(c => !fluentIndexKeys.Contains(IndexDedupeKey(c)))
            .Concat(indexes.Value)
            .ToList();
  ```

  Change the `entities` pipeline's `ApplyIndexes` call to use `mergedIndexConfigs` instead of `indexes.Value`:

  ```csharp
            .Select(entity => ModelMerger.ApplyIndexes(entity, mergedIndexConfigs))
  ```

  Add the dedupe-key helper as a private static method, next to the existing `RelationshipDedupeKey`:

  ```csharp
    private static (string EntityName, string PropertyNames) IndexDedupeKey(IndexConfig config)
    {
        return (config.EntityName, string.Join(",", config.PropertyNames));
    }
  ```

- [ ] **Step 8: Run tests to verify they pass**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~Build_IndexAttribute\|Build_FluentIndexAndAttribute`
  Expected: PASS (3 tests).

- [ ] **Step 9: Run the full test suite**

  Run: `dotnet test EfSchemaVisualizer.slnx`
  Expected: all tests PASS (previous count + 9 new tests).

- [ ] **Step 10: Commit**

  ```bash
  git add src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs \
          src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs \
          tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs \
          tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs
  git commit -m "Parse [Index] class attribute into EntityModel.Indexes, fluent HasIndex wins on conflict"
  ```

---

### Task 4: Value generation — parsing and model

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`
- Create: `src/EfSchemaVisualizer.Core/Merging/ValueGenerationConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Modify: `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.FindConfigurationScopes`, `FluentSyntaxHelpers.FindCallsNamed`, `FluentSyntaxHelpers.GetPropertyNameFor(InvocationExpressionSyntax fluentCall)` → `string?` (all existing, unchanged). `RecognizedCallNames` already contains the five value-generation call names from Task 1 Step 7.
- Produces: `PropertyModel.ValueGenerated` (`string?`, new trailing optional field). `ValueGenerationConfig(string EntityName, string PropertyName, string Mode)`. `FluentConfigParser.ParseValueGeneration(string sourceCode)` → `ParseResult<IReadOnlyList<ValueGenerationConfig>>`. `ModelMerger.ApplyValueGeneration(EntityModel entity, IReadOnlyList<ValueGenerationConfig> configs)` → `EntityModel`. Consumed by Task 5 (UI badge).

- [ ] **Step 1: Write a failing test for the new `PropertyModel.ValueGenerated` field**

  In `tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs`, add:

  ```csharp
    [Fact]
    public void ValueGenerated_DefaultsToNull()
    {
        var property = new PropertyModel("Id", "int", IsNullable: false, MaxLength: null);

        Assert.Null(property.ValueGenerated);
    }

    [Fact]
    public void WithValueGenerated_ProducesUpdatedCopy_LeavingOriginalUnchanged()
    {
        var original = new PropertyModel("Id", "int", IsNullable: false, MaxLength: null);

        var updated = original with { ValueGenerated = "Identity" };

        Assert.Null(original.ValueGenerated);
        Assert.Equal("Identity", updated.ValueGenerated);
        Assert.Equal(original.Name, updated.Name);
    }
  ```

- [ ] **Step 2: Run tests to verify they fail**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~ValueGenerated`
  Expected: FAIL with "ValueGenerated" not found on `PropertyModel` (compile error).

- [ ] **Step 3: Add `ValueGenerated` to `PropertyModel`**

  In `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`, add a new trailing parameter:

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
      string? DefaultValueLiteral = null,
      string? ValueGenerated = null,
      bool IsShadow = false);
  ```

  (`IsShadow` is added in the same step even though Task 6 uses it, since both are simple trailing-optional additions to the same record and splitting them into two separate edits of the same declaration would just create needless churn between tasks — but Task 6 still owns writing its own tests and parser/merge logic for `IsShadow`.)

- [ ] **Step 4: Run tests to verify they pass**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~ValueGenerated`
  Expected: PASS (2 tests). Also run the full model test file to confirm no other `PropertyModel` positional-argument construction broke: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~PropertyModelTests` → PASS.

- [ ] **Step 5: Create the `ValueGenerationConfig` DTO**

  Create `src/EfSchemaVisualizer.Core/Merging/ValueGenerationConfig.cs`:

  ```csharp
  namespace EfSchemaVisualizer.Core.Merging;

  public sealed record ValueGenerationConfig(string EntityName, string PropertyName, string Mode);
  ```

- [ ] **Step 6: Write failing tests for `FluentConfigParser.ParseValueGeneration`**

  In `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`, add:

  ```csharp
    // ─── ParseValueGeneration ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("ValueGeneratedOnAdd", "OnAdd")]
    [InlineData("ValueGeneratedOnUpdate", "OnUpdate")]
    [InlineData("ValueGeneratedOnAddOrUpdate", "OnAddOrUpdate")]
    [InlineData("ValueGeneratedNever", "Never")]
    [InlineData("UseIdentityColumn", "Identity")]
    public void ParseValueGeneration_EachRecognizedCall_MapsToExpectedMode(string callName, string expectedMode)
    {
        var source = $$"""
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.Property(e => e.Id).{{callName}}();
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseValueGeneration(source);

        var config = Assert.Single(result.Value);
        Assert.Equal("Person", config.EntityName);
        Assert.Equal("Id", config.PropertyName);
        Assert.Equal(expectedMode, config.Mode);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseValueGeneration_NoValueGenerationCalls_ReturnsEmpty()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.Property(e => e.Name).HasMaxLength(100);
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseValueGeneration(source);

        Assert.Empty(result.Value);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void ParseValueGeneration_IEntityTypeConfigurationStyle_ReadsMode()
    {
        const string source = """
            class PersonConfig : IEntityTypeConfiguration<Person> {
                public void Configure(EntityTypeBuilder<Person> builder) {
                    builder.Property(e => e.Id).UseIdentityColumn();
                }
            }
            """;

        var result = new FluentConfigParser().ParseValueGeneration(source);

        var config = Assert.Single(result.Value);
        Assert.Equal("Person", config.EntityName);
        Assert.Equal("Id", config.PropertyName);
        Assert.Equal("Identity", config.Mode);
    }
  ```

- [ ] **Step 7: Run tests to verify they fail**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~ParseValueGeneration`
  Expected: FAIL with "ParseValueGeneration" not found on `FluentConfigParser` (compile error).

- [ ] **Step 8: Implement `ParseValueGeneration`**

  In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, add (after `ParseIgnoredProperties`):

  ```csharp
    private static readonly Dictionary<string, string> ValueGenerationCallModes = new()
    {
        ["ValueGeneratedOnAdd"] = "OnAdd",
        ["ValueGeneratedOnUpdate"] = "OnUpdate",
        ["ValueGeneratedOnAddOrUpdate"] = "OnAddOrUpdate",
        ["ValueGeneratedNever"] = "Never",
        ["UseIdentityColumn"] = "Identity",
    };

    public ParseResult<IReadOnlyList<ValueGenerationConfig>> ParseValueGeneration(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<ValueGenerationConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var (callName, mode) in ValueGenerationCallModes)
            {
                foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, callName))
                {
                    var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(call);

                    if (propertyName is null)
                    {
                        diagnostics.Add(new Diagnostic(
                            DiagnosticCodes.UnresolvablePropertyName,
                            $"Could not resolve the property configured by '{callName}'.",
                            entityName,
                            PropertyName: null,
                            call.Span));
                        continue;
                    }

                    results.Add(new ValueGenerationConfig(entityName, propertyName, mode));
                }
            }
        }

        return new ParseResult<IReadOnlyList<ValueGenerationConfig>>(results, diagnostics);
    }
  ```

- [ ] **Step 9: Run tests to verify they pass**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~ParseValueGeneration`
  Expected: PASS (7 tests: 5 theory cases + 2 facts).

- [ ] **Step 10: Write a failing test for `ModelMerger.ApplyValueGeneration`**

  In `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`, add:

  ```csharp
    // ─── ApplyValueGeneration ──────────────────────────────────────────────────────

    [Fact]
    public void ApplyValueGeneration_SetsValueGeneratedOnMatchingProperty_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("Name", "string", IsNullable: true, MaxLength: null),
        });

        var configs = new List<ValueGenerationConfig>
        {
            new("Person", "Id", "Identity"),
            new("Address", "Line1", "OnAdd"), // different entity, must not affect Person
        };

        var merged = ModelMerger.ApplyValueGeneration(entity, configs);

        Assert.Equal("Identity", merged.Properties.Single(p => p.Name == "Id").ValueGenerated);
        Assert.Null(merged.Properties.Single(p => p.Name == "Name").ValueGenerated);
    }
  ```

- [ ] **Step 11: Run test to verify it fails**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~ApplyValueGeneration`
  Expected: FAIL with "ApplyValueGeneration" not found on `ModelMerger` (compile error).

- [ ] **Step 12: Implement `ModelMerger.ApplyValueGeneration`**

  In `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`, add (after `ApplyIgnoredProperties`):

  ```csharp
    public static EntityModel ApplyValueGeneration(EntityModel entity, IReadOnlyList<ValueGenerationConfig> configs)
    {
        var byProperty = IndexByProperty(entity.Name, configs, c => c.EntityName, c => c.PropertyName);

        var updatedProperties = entity.Properties
            .Select(property => byProperty.TryGetValue(property.Name, out var config)
                ? property with { ValueGenerated = config.Mode }
                : property)
            .ToList();

        return entity with { Properties = updatedProperties };
    }
  ```

- [ ] **Step 13: Run test to verify it passes**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~ApplyValueGeneration`
  Expected: PASS.

- [ ] **Step 14: Wire `ParseValueGeneration`/`ApplyValueGeneration` into `DiagramModelBuilder.Build`**

  In `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`, add the parse call alongside `ignoredProperties`:

  ```csharp
        var valueGeneration = configParser.ParseValueGeneration(configSource);
  ```

  Add its diagnostics:

  ```csharp
        diagnostics.AddRange(valueGeneration.Diagnostics);
  ```

  Add `.Select(entity => ModelMerger.ApplyValueGeneration(entity, valueGeneration.Value))` to the `entities` pipeline, before `ApplyIgnoredProperties` (order doesn't matter here since they touch different concerns, but keep it readable):

  ```csharp
            .Select(entity => ModelMerger.ApplyIndexes(entity, mergedIndexConfigs))
            .Select(entity => ModelMerger.ApplyValueGeneration(entity, valueGeneration.Value))
            .Select(entity => ModelMerger.ApplyIgnoredProperties(entity, ignoredProperties.Value))
  ```

- [ ] **Step 15: Write a failing end-to-end test**

  In `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs`, add:

  ```csharp
    [Fact]
    public void Build_UseIdentityColumn_SetsValueGeneratedOnProperty()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.Property(e => e.Id).UseIdentityColumn();
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var id = result.Entities.Single().Properties.Single(p => p.Name == "Id");
        Assert.Equal("Identity", id.ValueGenerated);
    }
  ```

- [ ] **Step 16: Run test to verify it passes**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~Build_UseIdentityColumn_SetsValueGeneratedOnProperty`
  Expected: PASS (wiring was already done in Step 14, so this should pass immediately — confirm).

- [ ] **Step 17: Run the full test suite**

  Run: `dotnet test EfSchemaVisualizer.slnx`
  Expected: all tests PASS (previous count + 11 new tests).

- [ ] **Step 18: Commit**

  ```bash
  git add src/EfSchemaVisualizer.Core/Model/PropertyModel.cs \
          src/EfSchemaVisualizer.Core/Merging/ValueGenerationConfig.cs \
          src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs \
          src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs \
          src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs \
          tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs \
          tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs \
          tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs \
          tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs
  git commit -m "Parse and merge EF value-generation calls into PropertyModel.ValueGenerated"
  ```

---

### Task 5: Value generation — read-only diagram badge

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`
- Test: `tests/EfSchemaVisualizer.Web.Tests/Diagram/EntityNodeAccessibilityTests.cs` (rename class to a more general markup-assertions file, or add a sibling test file — see Step 3 below)

**Interfaces:**
- Consumes: `PropertyModel.ValueGenerated` (`string?`, from Task 4).
- Produces: nothing consumed elsewhere — this is a leaf UI change.

- [ ] **Step 1: Locate the property row markup and plan the badge insertion point**

  In `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`, the property type span currently reads (around line 92-93):

  ```razor
                    <span @ondblclick="() => BeginTypeEdit(property)" title="Double-click to edit type"
                          style="cursor: text; border-bottom: 1px dashed #999;">@property.ClrType@(property.IsNullable ? "?" : "")</span>
  ```

  The badge goes immediately after this `</span>`, before the `nullable` checkbox `<label>`.

- [ ] **Step 2: Add the read-only value-generation badge**

  Insert this block immediately after the type-display `<span>...</span>` closing tag from Step 1 (still inside the same `<li>`, before the `<label style="font-size: 0.8em; margin-left: 4px;">` nullable checkbox):

  ```razor
                @if (property.ValueGenerated is not null)
                {
                    <span class="value-generated-badge" title="EF value generation: @property.ValueGenerated"
                          style="font-size: 0.7em; background: #ddf; border-radius: 3px; padding: 1px 4px; margin-left: 4px;">@property.ValueGenerated</span>
                }
  ```

- [ ] **Step 3: Write a failing markup-source test asserting the badge exists**

  Following the precedent in `tests/EfSchemaVisualizer.Web.Tests/Diagram/EntityNodeAccessibilityTests.cs` (full bUnit rendering isn't viable for this component — its `PortRenderer` children throw during `OnAfterRenderAsync` under bUnit's headless render tree — so assertions run directly against the `.razor` source), create `tests/EfSchemaVisualizer.Web.Tests/Diagram/EntityNodeMarkupTests.cs`:

  ```csharp
  namespace EfSchemaVisualizer.Web.Tests.Diagram;

  /// Markup-source assertions for EntityNode.razor features that can't be exercised via full bUnit
  /// rendering (see EntityNodeAccessibilityTests for why). Each test pins down a specific rendering
  /// invariant the component's @code and markup must uphold.
  public class EntityNodeMarkupTests
  {
      [Fact]
      public void PropertyRow_RendersValueGeneratedBadge_WhenValueGeneratedIsSet()
      {
          var markup = ReadEntityNodeRazorSource();

          Assert.Contains("property.ValueGenerated is not null", markup);
          Assert.Contains("value-generated-badge", markup);
      }

      private static string ReadEntityNodeRazorSource()
      {
          var path = Path.Combine(FindRepoRoot(), "src", "EfSchemaVisualizer.Web", "Diagram", "EntityNode.razor");
          return File.ReadAllText(path);
      }

      private static string FindRepoRoot()
      {
          var dir = new DirectoryInfo(AppContext.BaseDirectory);
          while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EfSchemaVisualizer.slnx")))
          {
              dir = dir.Parent;
          }

          return dir?.FullName
              ?? throw new InvalidOperationException("Could not locate repo root (EfSchemaVisualizer.slnx) above " + AppContext.BaseDirectory);
      }
  }
  ```

  Note: since Step 2 already added the markup, write this test file first with the assertion, run it — it should PASS immediately since Step 2 precedes it. To genuinely see it fail first, temporarily comment out the `@if (property.ValueGenerated is not null)` block, run the test (FAIL), then restore it (PASS). This is the pragmatic TDD-in-markup equivalent here since there's no separate "fail then implement" gap for a source-text assertion once the source already has the content.

- [ ] **Step 4: Run the test to verify it passes**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~PropertyRow_RendersValueGeneratedBadge`
  Expected: PASS.

- [ ] **Step 5: Manually verify the badge renders correctly in a browser**

  Run: `dotnet run --project src/EfSchemaVisualizer.Web` (or use the project's existing `run` skill if one is configured), paste a class source with an `Id` property and a config source calling `.Property(e => e.Id).UseIdentityColumn()`, and confirm an "Identity" badge appears next to the `Id` property's type in the rendered diagram, with no console errors.

- [ ] **Step 6: Run the full test suite**

  Run: `dotnet test EfSchemaVisualizer.slnx`
  Expected: all tests PASS (previous count + 1 new test).

- [ ] **Step 7: Commit**

  ```bash
  git add src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor \
          tests/EfSchemaVisualizer.Web.Tests/Diagram/EntityNodeMarkupTests.cs
  git commit -m "Show a read-only value-generation badge on property rows in the diagram"
  ```

---

### Task 6: Shadow properties — parsing and model

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Create: `src/EfSchemaVisualizer.Core/Merging/ShadowPropertyConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.FindConfigurationScopes`, `FluentSyntaxHelpers.FindCallsNamed(scope, "Property")` (existing — `FindCallsNamed` already matches generic `Property<T>(...)` calls too, since it filters on `Name.Identifier.Text`, which is `"Property"` regardless of a `GenericNameSyntax`'s type arguments). `PropertyModel.IsShadow` (added by Task 4 Step 3).
- Produces: `ShadowPropertyConfig(string EntityName, string PropertyName, string ClrType)`. `FluentConfigParser.ParseShadowProperties(string sourceCode)` → `ParseResult<IReadOnlyList<ShadowPropertyConfig>>`. `ModelMerger.ApplyShadowProperties(EntityModel entity, IReadOnlyList<ShadowPropertyConfig> configs)` → `EntityModel`. Consumed by Task 7 (UI read-only row).

- [ ] **Step 1: Write a failing test confirming `PropertyModel.IsShadow` defaults correctly**

  In `tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs`, add:

  ```csharp
    [Fact]
    public void IsShadow_DefaultsToFalse()
    {
        var property = new PropertyModel("Id", "int", IsNullable: false, MaxLength: null);

        Assert.False(property.IsShadow);
    }

    [Fact]
    public void WithIsShadow_ProducesUpdatedCopy_LeavingOriginalUnchanged()
    {
        var original = new PropertyModel("CreatedBy", "string", IsNullable: true, MaxLength: null);

        var updated = original with { IsShadow = true };

        Assert.False(original.IsShadow);
        Assert.True(updated.IsShadow);
    }
  ```

  (`IsShadow` already exists on `PropertyModel` as of Task 4 Step 3, so these tests should PASS immediately — this step exists to give the field its own explicit regression coverage, separate from `ValueGenerated`'s.)

- [ ] **Step 2: Run tests to verify they pass**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~IsShadow`
  Expected: PASS (2 tests).

- [ ] **Step 3: Create the `ShadowPropertyConfig` DTO**

  Create `src/EfSchemaVisualizer.Core/Merging/ShadowPropertyConfig.cs`:

  ```csharp
  namespace EfSchemaVisualizer.Core.Merging;

  public sealed record ShadowPropertyConfig(string EntityName, string PropertyName, string ClrType);
  ```

- [ ] **Step 4: Write failing tests for `FluentConfigParser.ParseShadowProperties`**

  In `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`, add:

  ```csharp
    // ─── ParseShadowProperties ─────────────────────────────────────────────────────

    [Fact]
    public void ParseShadowProperties_GenericPropertyWithStringLiteralName_ReadsNameAndType()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.Property<string>("CreatedBy");
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseShadowProperties(source);

        var config = Assert.Single(result.Value);
        Assert.Equal("Person", config.EntityName);
        Assert.Equal("CreatedBy", config.PropertyName);
        Assert.Equal("string", config.ClrType);
    }

    [Fact]
    public void ParseShadowProperties_NonGenericPropertyCall_NotTreatedAsShadow()
    {
        const string source = """
            class Ctx : DbContext {
                protected override void OnModelCreating(ModelBuilder modelBuilder) {
                    modelBuilder.Entity<Person>(entity => {
                        entity.Property(e => e.Name).HasMaxLength(100);
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseShadowProperties(source);

        Assert.Empty(result.Value);
    }

    [Fact]
    public void ParseShadowProperties_IEntityTypeConfigurationStyle_ReadsNameAndType()
    {
        const string source = """
            class PersonConfig : IEntityTypeConfiguration<Person> {
                public void Configure(EntityTypeBuilder<Person> builder) {
                    builder.Property<DateTime>("LastModified");
                }
            }
            """;

        var result = new FluentConfigParser().ParseShadowProperties(source);

        var config = Assert.Single(result.Value);
        Assert.Equal("Person", config.EntityName);
        Assert.Equal("LastModified", config.PropertyName);
        Assert.Equal("DateTime", config.ClrType);
    }
  ```

- [ ] **Step 5: Run tests to verify they fail**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~ParseShadowProperties`
  Expected: FAIL with "ParseShadowProperties" not found on `FluentConfigParser` (compile error).

- [ ] **Step 6: Implement `ParseShadowProperties`**

  In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, add (after `ParseValueGeneration`):

  ```csharp
    public ParseResult<IReadOnlyList<ShadowPropertyConfig>> ParseShadowProperties(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<ShadowPropertyConfig>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root))
        {
            foreach (var propertyCall in FluentSyntaxHelpers.FindCallsNamed(scope, "Property"))
            {
                if (propertyCall.Expression is not MemberAccessExpressionSyntax
                    {
                        Name: GenericNameSyntax { TypeArgumentList.Arguments: [var typeArgNode] },
                    })
                {
                    continue;
                }

                if (propertyCall.ArgumentList.Arguments.FirstOrDefault()?.Expression is not LiteralExpressionSyntax literal
                    || !literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    continue;
                }

                results.Add(new ShadowPropertyConfig(entityName, literal.Token.ValueText, typeArgNode.ToString()));
            }
        }

        return new ParseResult<IReadOnlyList<ShadowPropertyConfig>>(results, new List<Diagnostic>());
    }
  ```

- [ ] **Step 7: Run tests to verify they pass**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~ParseShadowProperties`
  Expected: PASS (3 tests).

- [ ] **Step 8: Write failing tests for `ModelMerger.ApplyShadowProperties`**

  In `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`, add:

  ```csharp
    // ─── ApplyShadowProperties ─────────────────────────────────────────────────────

    [Fact]
    public void ApplyShadowProperties_AppendsSynthesizedPropertyForUnmatchedConfig()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
        });

        var configs = new List<ShadowPropertyConfig>
        {
            new("Person", "CreatedBy", "string"),
            new("Address", "Line1", "string"), // different entity, must not affect Person
        };

        var merged = ModelMerger.ApplyShadowProperties(entity, configs);

        Assert.Equal(2, merged.Properties.Count);
        var shadow = merged.Properties.Single(p => p.Name == "CreatedBy");
        Assert.Equal("string", shadow.ClrType);
        Assert.True(shadow.IsShadow);
    }

    [Fact]
    public void ApplyShadowProperties_NameCollidesWithExistingProperty_DoesNotSynthesizeDuplicate()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
            new("CreatedBy", "string", IsNullable: true, MaxLength: null),
        });

        var configs = new List<ShadowPropertyConfig>
        {
            new("Person", "CreatedBy", "string"),
        };

        var merged = ModelMerger.ApplyShadowProperties(entity, configs);

        Assert.Equal(2, merged.Properties.Count);
        Assert.False(merged.Properties.Single(p => p.Name == "CreatedBy").IsShadow);
    }

    [Fact]
    public void ApplyShadowProperties_NoMatchingConfig_ReturnsEntityUnchanged()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Id", "int", IsNullable: false, MaxLength: null),
        });

        var merged = ModelMerger.ApplyShadowProperties(entity, new List<ShadowPropertyConfig>());

        Assert.Single(merged.Properties);
    }
  ```

- [ ] **Step 9: Run tests to verify they fail**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~ApplyShadowProperties`
  Expected: FAIL with "ApplyShadowProperties" not found on `ModelMerger` (compile error).

- [ ] **Step 10: Implement `ModelMerger.ApplyShadowProperties`**

  In `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`, add (after `ApplyValueGeneration`):

  ```csharp
    public static EntityModel ApplyShadowProperties(EntityModel entity, IReadOnlyList<ShadowPropertyConfig> configs)
    {
        var existingNames = entity.Properties.Select(p => p.Name).ToHashSet();

        var shadowProperties = configs
            .Where(c => c.EntityName == entity.Name && !existingNames.Contains(c.PropertyName))
            .Select(c => new PropertyModel(c.PropertyName, c.ClrType, IsNullable: true, MaxLength: null, IsShadow: true))
            .ToList();

        if (shadowProperties.Count == 0)
        {
            return entity;
        }

        return entity with { Properties = entity.Properties.Concat(shadowProperties).ToList() };
    }
  ```

- [ ] **Step 11: Run tests to verify they pass**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~ApplyShadowProperties`
  Expected: PASS (3 tests).

- [ ] **Step 12: Wire `ParseShadowProperties`/`ApplyShadowProperties` into `DiagramModelBuilder.Build`**

  In `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`, add the parse call alongside `valueGeneration`:

  ```csharp
        var shadowProperties = configParser.ParseShadowProperties(configSource);
  ```

  Add its diagnostics:

  ```csharp
        diagnostics.AddRange(shadowProperties.Diagnostics);
  ```

  Add `.Select(entity => ModelMerger.ApplyShadowProperties(entity, shadowProperties.Value))` as the **last** step in the `entities` pipeline (after `ApplyIgnoredProperties`, so a property removed by `Ignore` doesn't block a same-named shadow-property synthesis, and so shadow synthesis sees the final property list when checking for name collisions):

  ```csharp
            .Select(entity => ModelMerger.ApplyIgnoredProperties(entity, ignoredProperties.Value))
            .Select(entity => ModelMerger.ApplyShadowProperties(entity, shadowProperties.Value))
            .ToList();
  ```

- [ ] **Step 13: Write a failing end-to-end test**

  In `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs`, add:

  ```csharp
    [Fact]
    public void Build_ShadowProperty_AppearsAsIsShadowProperty()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.Property<string>("CreatedBy");
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var shadow = result.Entities.Single().Properties.Single(p => p.Name == "CreatedBy");
        Assert.True(shadow.IsShadow);
        Assert.Equal("string", shadow.ClrType);
    }
  ```

- [ ] **Step 14: Run test to verify it passes**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~Build_ShadowProperty_AppearsAsIsShadowProperty`
  Expected: PASS (wiring done in Step 12 — confirm it passes on first run).

- [ ] **Step 15: Run the full test suite**

  Run: `dotnet test EfSchemaVisualizer.slnx`
  Expected: all tests PASS (previous count + 9 new tests).

- [ ] **Step 16: Commit**

  ```bash
  git add src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs \
          src/EfSchemaVisualizer.Core/Merging/ShadowPropertyConfig.cs \
          src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs \
          src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs \
          tests/EfSchemaVisualizer.Core.Tests/Model/PropertyModelTests.cs \
          tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs \
          tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs \
          tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs
  git commit -m "Parse shadow properties and synthesize them into EntityModel.Properties"
  ```

---

### Task 7: Shadow properties — read-only diagram row

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`
- Modify: `tests/EfSchemaVisualizer.Web.Tests/Diagram/EntityNodeMarkupTests.cs`

**Interfaces:**
- Consumes: `PropertyModel.IsShadow` (from Task 6).
- Produces: nothing consumed elsewhere — this is a leaf UI change, completing the four-feature slice.

- [ ] **Step 1: Wrap the existing property `<li>` body in a shadow/non-shadow branch**

  In `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`, the current property loop looks like:

  ```razor
        @foreach (var property in Node.Entity.Properties)
        {
            var isKey = Node.Entity.KeyPropertyNames.Contains(property.Name);
            <li style="padding: 2px 8px; @(isKey ? "font-weight: bold;" : "")">
                @if (isKey)
                {
                    <text>🔑 </text>
                }
                @if (_editingPropertyName == property.Name)
                {
                    ... (full editable row content)
                }
            </li>
        }
  ```

  Change the `<li>` to branch on `property.IsShadow` right after the opening tag, keeping the existing editable body under the `else`:

  ```razor
        @foreach (var property in Node.Entity.Properties)
        {
            var isKey = Node.Entity.KeyPropertyNames.Contains(property.Name);
            <li style="padding: 2px 8px; @(isKey ? "font-weight: bold;" : "")">
                @if (property.IsShadow)
                {
                    <span class="shadow-property" title="Shadow property: configured in code but has no matching CLR member. Read-only here."
                          style="font-style: italic; color: #888;">@property.Name : @property.ClrType@(property.IsNullable ? "?" : "") (shadow)</span>
                }
                else
                {
                    @if (isKey)
                    {
                        <text>🔑 </text>
                    }
                    @if (_editingPropertyName == property.Name)
                    {
                        ... (existing editable row content, unchanged, now nested one level deeper under this else)
                    }
                }
            </li>
        }
  ```

  Concretely: take everything currently between `@if (isKey) { <text>🔑 </text> }` and the closing `</li>` (i.e. the whole rest of the existing property row body, ending right before `</li>`) and indent it one level under a new `else` branch, with the `@if (property.IsShadow) { ... }` block inserted as the `if` branch above it. Do not change any of that existing body's content — only its indentation and the wrapping `if`/`else`.

- [ ] **Step 2: Write a failing markup-source test asserting the shadow row exists**

  In `tests/EfSchemaVisualizer.Web.Tests/Diagram/EntityNodeMarkupTests.cs`, add:

  ```csharp
      [Fact]
      public void PropertyRow_RendersReadOnlyShadowRow_WhenIsShadowIsSet()
      {
          var markup = ReadEntityNodeRazorSource();

          Assert.Contains("property.IsShadow", markup);
          Assert.Contains("shadow-property", markup);
      }
  ```

  As with Task 5 Step 3, verify this genuinely exercises the change by temporarily reverting Step 1's edit, running the test (FAIL), then restoring it (PASS).

- [ ] **Step 3: Run the test to verify it passes**

  Run: `dotnet test EfSchemaVisualizer.slnx --filter FullyQualifiedName~PropertyRow_RendersReadOnlyShadowRow`
  Expected: PASS.

- [ ] **Step 4: Manually verify the shadow row renders correctly and doesn't break existing rows**

  Run: `dotnet run --project src/EfSchemaVisualizer.Web`, paste a class source with a normal `Id` property and a config source calling `entity.Property<string>("CreatedBy")`, and confirm: the `Id` row still shows all its normal editable affordances (rename, retype, nullable checkbox, expand-panel, remove button); the `CreatedBy` row renders as a dimmed/italic read-only line with no editable controls; no console errors.

- [ ] **Step 5: Run the full test suite**

  Run: `dotnet test EfSchemaVisualizer.slnx`
  Expected: all tests PASS (previous count + 1 new test). This is the final task in the plan — confirm the full suite is green end to end.

- [ ] **Step 6: Commit**

  ```bash
  git add src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor \
          tests/EfSchemaVisualizer.Web.Tests/Diagram/EntityNodeMarkupTests.cs
  git commit -m "Render shadow properties as read-only rows in the diagram"
  ```

---

## Backlog update (after all tasks complete)

Once all seven tasks are done and the full suite is green, update `docs/backlog.md`'s Round 3 Priority 3 section: mark the four completed items (`Ignore`/`Ignore<T>()`, `[Index]` attribute, value generation, shadow properties) as `[x]` with an `**Update:**` note in the same style as every other completed item in that file, referencing this plan. This is a documentation-only step, not a code task — do it as a final small commit after Task 7's commit, not as part of any task above.
