# Inheritance Mapping Strategy & Discriminator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Parse, model, edit, rewrite, and render EF Core's `HasDiscriminator`/`HasValue` (TPH discriminator config) and `UseTptMappingStrategy()`/`UseTpcMappingStrategy()` (TPT/TPC mapping strategy), completing backlog item W2's deferred remainder.

**Architecture:** Two new `FluentConfigParser` extractors feed two new `ModelMerger.Apply*` steps that stamp per-entity `MappingStrategy`/discriminator fields onto `EntityModel` before `InheritanceInference.Fold` runs; `Fold` resolves one strategy per whole hierarchy (root-priority, flagging any inconsistency) and branches its property-folding behavior on it (TPT folds only the inherited key; TPH/TPC fold everything, unchanged from today). Two new `OnModelCreatingRewriter` methods (`SetMappingStrategy`/`RemoveMappingStrategy`, `SetDiscriminator`/`RemoveDiscriminator`) follow the existing find-scope-mutate-or-insert pattern already used by `SetDefaultValueSql`/`SetSequence`. `DiagramEditor` exposes five new edit methods with a symmetric guard blocking strategy-vs-discriminator conflicts. `EntityNode.razor` gets a mapping-strategy dropdown (every hierarchy member) and a discriminator panel (root only); `DiagramSync.cs` suppresses the inheritance link only for TPC.

**Tech Stack:** C#/.NET, Roslyn (`Microsoft.CodeAnalysis.CSharp`) for parsing/rewriting, Blazor for the diagram UI, xUnit for tests.

## Global Constraints

- Every new/changed record field is additive with a default value — no existing positional or named `EntityModel`/`RelationshipModel`/`InheritanceFoldResult` construction anywhere in `src/` or `tests/` may need updating.
- Only `HasDiscriminator<T>("Name")` and `HasDiscriminator("Name")` (implicit `string` type) are parsed — not the non-generic `HasDiscriminator(Type, string)` overload, and not the zero-argument convention overload.
- Switching mapping strategy away from `Tph` is **blocked** (not auto-cleared) when discriminator config exists on the hierarchy, and vice versa — no silent deletion of user-written config, in either direction.
- Switching strategy only adds/removes the single `UseTptMappingStrategy()`/`UseTpcMappingStrategy()` call — never synthesizes `ToTable(...)` calls.
- `NormalizeWhitespace()` usage, statement-insertion helpers (`FindConfigScopes`, `GetScopeBlockAndReceiver`, `BuildEntityInvocationStatement`, `FindOnModelCreatingMethod`), and the `DiagramEditResult.Ok()/Fail(...)` return convention must match the existing code exactly — do not introduce a parallel convention.
- Full test suite (`dotnet test`) must stay green after every task.

---

### Task 1: Model & merging scaffolding

**Files:**
- Create: `src/EfSchemaVisualizer.Core/Model/MappingStrategy.cs`
- Modify: `src/EfSchemaVisualizer.Core/Model/EntityModel.cs`
- Create: `src/EfSchemaVisualizer.Core/Merging/MappingStrategyConfig.cs`
- Create: `src/EfSchemaVisualizer.Core/Merging/DiscriminatorColumnConfig.cs`
- Create: `src/EfSchemaVisualizer.Core/Merging/DiscriminatorValueConfig.cs`
- Modify: `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`

**Interfaces:**
- Produces: `MappingStrategy` enum (`Tph = 0`, `Tpt`, `Tpc`); `EntityModel.MappingStrategy` (default `MappingStrategy.Tph`), `EntityModel.DiscriminatorPropertyName`/`DiscriminatorClrType`/`DiscriminatorValue` (all `string?`, default `null`); `MappingStrategyConfig(string EntityName, MappingStrategy Strategy)`; `DiscriminatorColumnConfig(string EntityName, string ColumnName, string ClrType)`; `DiscriminatorValueConfig(string EntityName, string Value)`; `ModelMerger.ApplyMappingStrategies(EntityModel, IReadOnlyList<MappingStrategyConfig>)`, `ModelMerger.ApplyDiscriminatorColumn(EntityModel, IReadOnlyList<DiscriminatorColumnConfig>)`, `ModelMerger.ApplyDiscriminatorValue(EntityModel, IReadOnlyList<DiscriminatorValueConfig>)` — all `EntityModel`-returning, matching the existing `ApplyKeys`/`ApplyCheckConstraints` shape. New `DiagnosticCodes.UnreadableHasDiscriminatorArgument`, `UnreadableHasValueArgument`, `InconsistentMappingStrategyInHierarchy`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`:

```csharp
    [Fact]
    public void ApplyMappingStrategies_SetsMappingStrategyOnMatchingEntity_DefaultsToTphOtherwise()
    {
        var person = new EntityModel("Person", new List<PropertyModel>());
        var order = new EntityModel("Order", new List<PropertyModel>());

        var configs = new List<MappingStrategyConfig> { new("Person", MappingStrategy.Tpt) };

        Assert.Equal(MappingStrategy.Tpt, ModelMerger.ApplyMappingStrategies(person, configs).MappingStrategy);
        Assert.Equal(MappingStrategy.Tph, ModelMerger.ApplyMappingStrategies(order, configs).MappingStrategy);
    }

    [Fact]
    public void ApplyDiscriminatorColumn_SetsPropertyNameAndClrType_LeavesNonMatchingEntityUntouched()
    {
        var person = new EntityModel("Person", new List<PropertyModel>());
        var order = new EntityModel("Order", new List<PropertyModel>());

        var configs = new List<DiscriminatorColumnConfig> { new("Person", "Discriminator", "string") };

        var merged = ModelMerger.ApplyDiscriminatorColumn(person, configs);
        Assert.Equal("Discriminator", merged.DiscriminatorPropertyName);
        Assert.Equal("string", merged.DiscriminatorClrType);

        Assert.Null(ModelMerger.ApplyDiscriminatorColumn(order, configs).DiscriminatorPropertyName);
    }

    [Fact]
    public void ApplyDiscriminatorValue_SetsValueOnMatchingEntity_LeavesNonMatchingEntityUntouched()
    {
        var student = new EntityModel("Student", new List<PropertyModel>());
        var teacher = new EntityModel("Teacher", new List<PropertyModel>());

        var configs = new List<DiscriminatorValueConfig> { new("Student", "\"S\"") };

        Assert.Equal("\"S\"", ModelMerger.ApplyDiscriminatorValue(student, configs).DiscriminatorValue);
        Assert.Null(ModelMerger.ApplyDiscriminatorValue(teacher, configs).DiscriminatorValue);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ModelMergerTests"`
Expected: FAIL to compile — `MappingStrategy`, `MappingStrategyConfig`, `DiscriminatorColumnConfig`, `DiscriminatorValueConfig`, `ApplyMappingStrategies`, `ApplyDiscriminatorColumn`, `ApplyDiscriminatorValue` don't exist yet.

- [ ] **Step 3: Create `MappingStrategy.cs`**

```csharp
namespace EfSchemaVisualizer.Core.Model;

public enum MappingStrategy
{
    Tph,
    Tpt,
    Tpc,
}
```

- [ ] **Step 4: Add fields to `EntityModel.cs`**

Add four new optional parameters at the end of the primary constructor parameter list (after `CheckConstraints`):

```csharp
    IReadOnlyList<CheckConstraintModel>? CheckConstraints = null,
    MappingStrategy MappingStrategy = MappingStrategy.Tph,
    string? DiscriminatorPropertyName = null,
    string? DiscriminatorClrType = null,
    string? DiscriminatorValue = null)
```

No other changes to the file are needed — the existing `{ get; init; } = X ?? new List<...>()` body only applies to collection-typed members.

- [ ] **Step 5: Create the three `Merging` config records**

`src/EfSchemaVisualizer.Core/Merging/MappingStrategyConfig.cs`:

```csharp
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Core.Merging;

public sealed record MappingStrategyConfig(string EntityName, MappingStrategy Strategy);
```

`src/EfSchemaVisualizer.Core/Merging/DiscriminatorColumnConfig.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Merging;

public sealed record DiscriminatorColumnConfig(string EntityName, string ColumnName, string ClrType);
```

`src/EfSchemaVisualizer.Core/Merging/DiscriminatorValueConfig.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Merging;

public sealed record DiscriminatorValueConfig(string EntityName, string Value);
```

- [ ] **Step 6: Add the three `Apply*` methods to `ModelMerger.cs`**

Add near `ApplyCheckConstraints`:

```csharp
    public static EntityModel ApplyMappingStrategies(EntityModel entity, IReadOnlyList<MappingStrategyConfig> configs)
    {
        var config = configs.FirstOrDefault(c => c.EntityName == entity.Name);
        return config is null ? entity : entity with { MappingStrategy = config.Strategy };
    }

    public static EntityModel ApplyDiscriminatorColumn(EntityModel entity, IReadOnlyList<DiscriminatorColumnConfig> configs)
    {
        var config = configs.FirstOrDefault(c => c.EntityName == entity.Name);
        return config is null
            ? entity
            : entity with { DiscriminatorPropertyName = config.ColumnName, DiscriminatorClrType = config.ClrType };
    }

    public static EntityModel ApplyDiscriminatorValue(EntityModel entity, IReadOnlyList<DiscriminatorValueConfig> configs)
    {
        var config = configs.FirstOrDefault(c => c.EntityName == entity.Name);
        return config is null ? entity : entity with { DiscriminatorValue = config.Value };
    }
```

- [ ] **Step 7: Add the three new diagnostic codes**

In `DiagnosticCodes.cs`, add near `UnreadableHasCheckConstraintArgument`:

```csharp
    public const string UnreadableHasDiscriminatorArgument = nameof(UnreadableHasDiscriminatorArgument);
    public const string UnreadableHasValueArgument = nameof(UnreadableHasValueArgument);
```

And in the model-validity block (after `ForeignKeyTargetsKeylessPrincipal`):

```csharp
    public const string InconsistentMappingStrategyInHierarchy = nameof(InconsistentMappingStrategyInHierarchy);
```

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ModelMergerTests"`
Expected: PASS (all `ModelMergerTests`, including the three new ones).

- [ ] **Step 9: Run full suite to confirm no regressions**

Run: `dotnet test`
Expected: PASS, same total count as before plus 3.

- [ ] **Step 10: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Model/MappingStrategy.cs src/EfSchemaVisualizer.Core/Model/EntityModel.cs src/EfSchemaVisualizer.Core/Merging/MappingStrategyConfig.cs src/EfSchemaVisualizer.Core/Merging/DiscriminatorColumnConfig.cs src/EfSchemaVisualizer.Core/Merging/DiscriminatorValueConfig.cs src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs
git commit -m "Add MappingStrategy/discriminator model fields and merger steps"
```

---

### Task 2: `FluentConfigParser` — parse mapping strategy calls and discriminator chains

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`

**Interfaces:**
- Consumes: `MappingStrategy` (Task 1), `MappingStrategyConfig`/`DiscriminatorColumnConfig`/`DiscriminatorValueConfig` (Task 1), `DiagnosticCodes.UnreadableHasDiscriminatorArgument`/`UnreadableHasValueArgument` (Task 1), `FluentSyntaxHelpers.FindConfigurationScopes`, `FindCallsNamed`, `WalkChainedTail` (existing).
- Produces: `FluentConfigParser.ParseMappingStrategies(string) -> ParseResult<IReadOnlyList<MappingStrategyConfig>>`; `FluentConfigParser.ParseDiscriminators(string) -> ParseResult<(IReadOnlyList<DiscriminatorColumnConfig> Columns, IReadOnlyList<DiscriminatorValueConfig> Values)>`.

- [ ] **Step 1: Write the failing tests**

Add to `FluentConfigParserTests.cs`:

```csharp
    private const string MappingStrategySource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.UseTptMappingStrategy();
                });

                modelBuilder.Entity<Student>(entity => { });
            }
        }
        """;

    [Fact]
    public void ParseMappingStrategies_ReadsUseTptMappingStrategy()
    {
        var result = new FluentConfigParser().ParseMappingStrategies(MappingStrategySource);

        Assert.Empty(result.Diagnostics);
        var config = Assert.Single(result.Value);
        Assert.Equal("Person", config.EntityName);
        Assert.Equal(MappingStrategy.Tpt, config.Strategy);
    }

    private const string TpcMappingStrategySource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.UseTpcMappingStrategy();
                });
            }
        }
        """;

    [Fact]
    public void ParseMappingStrategies_ReadsUseTpcMappingStrategy()
    {
        var result = new FluentConfigParser().ParseMappingStrategies(TpcMappingStrategySource);

        var config = Assert.Single(result.Value);
        Assert.Equal(MappingStrategy.Tpc, config.Strategy);
    }

    private const string DiscriminatorSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.HasDiscriminator<string>("Type").HasValue<Student>("S").HasValue<Teacher>("T");
                });
            }
        }
        """;

    [Fact]
    public void ParseDiscriminators_ReadsColumnAndEveryChainedHasValue()
    {
        var result = new FluentConfigParser().ParseDiscriminators(DiscriminatorSource);

        Assert.Empty(result.Diagnostics);
        var column = Assert.Single(result.Value.Columns);
        Assert.Equal("Person", column.EntityName);
        Assert.Equal("Type", column.ColumnName);
        Assert.Equal("string", column.ClrType);

        Assert.Equal(2, result.Value.Values.Count);
        Assert.Contains(result.Value.Values, v => v.EntityName == "Student" && v.Value == "\"S\"");
        Assert.Contains(result.Value.Values, v => v.EntityName == "Teacher" && v.Value == "\"T\"");
    }

    private const string ImplicitStringDiscriminatorSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.HasDiscriminator("Type");
                });
            }
        }
        """;

    [Fact]
    public void ParseDiscriminators_NonGenericOverload_DefaultsClrTypeToString()
    {
        var result = new FluentConfigParser().ParseDiscriminators(ImplicitStringDiscriminatorSource);

        var column = Assert.Single(result.Value.Columns);
        Assert.Equal("string", column.ClrType);
        Assert.Empty(result.Value.Values);
    }

    private const string UnreadableDiscriminatorSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.HasDiscriminator<string>(SomeMethod());
                });
            }
        }
        """;

    [Fact]
    public void ParseDiscriminators_UnreadableColumnArgument_ProducesDiagnostic()
    {
        var result = new FluentConfigParser().ParseDiscriminators(UnreadableDiscriminatorSource);

        Assert.Empty(result.Value.Columns);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnreadableHasDiscriminatorArgument, diagnostic.Code);
    }

    [Fact]
    public void ParseUnrecognizedCalls_HasValueChainedOntoSomethingElse_StillFlagsUnrecognized()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.HasAlternateKey(e => e.Ssn).HasValue<Student>("S");
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseUnrecognizedCalls(source);

        Assert.Contains(result, d => d.Message.Contains("'HasValue'"));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~FluentConfigParserTests"`
Expected: FAIL to compile — `ParseMappingStrategies`/`ParseDiscriminators` don't exist yet; the last test fails at runtime (currently `HasValue` isn't recognized as a call name at all, so nothing is flagged the way the test expects — verify this is the actual current failure mode before proceeding).

- [ ] **Step 3: Add recognized-call-name entries**

In `FluentConfigParser.cs`, add to `RecognizedCallNames` (after `"UseSequence"`):

```csharp
        "UseTptMappingStrategy", "UseTpcMappingStrategy", "HasDiscriminator",
```

Add to `ContextSensitiveCallNames`:

```csharp
        ["HasValue"] = new HashSet<string> { "HasDiscriminator" },
```

- [ ] **Step 4: Implement `ParseMappingStrategies`**

Add after `ParseUseSequences`:

```csharp
    public ParseResult<IReadOnlyList<MappingStrategyConfig>> ParseMappingStrategies(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<MappingStrategyConfig>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root, _entities))
        {
            if (FluentSyntaxHelpers.FindCallsNamed(scope, "UseTptMappingStrategy").Any())
            {
                results.Add(new MappingStrategyConfig(entityName, MappingStrategy.Tpt));
            }
            else if (FluentSyntaxHelpers.FindCallsNamed(scope, "UseTpcMappingStrategy").Any())
            {
                results.Add(new MappingStrategyConfig(entityName, MappingStrategy.Tpc));
            }
        }

        return new ParseResult<IReadOnlyList<MappingStrategyConfig>>(results, Array.Empty<Diagnostic>());
    }
```

- [ ] **Step 5: Implement `ParseDiscriminators`**

```csharp
    public ParseResult<(IReadOnlyList<DiscriminatorColumnConfig> Columns, IReadOnlyList<DiscriminatorValueConfig> Values)> ParseDiscriminators(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var columns = new List<DiscriminatorColumnConfig>();
        var values = new List<DiscriminatorValueConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root, _entities))
        {
            foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "HasDiscriminator"))
            {
                var nameArg = call.ArgumentList.Arguments.FirstOrDefault();
                if (nameArg?.Expression is not LiteralExpressionSyntax nameLiteral || !nameLiteral.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnreadableHasDiscriminatorArgument,
                        "HasDiscriminator column-name argument is not a string literal and could not be read.",
                        entityName,
                        PropertyName: null,
                        call.Span));
                    continue;
                }

                var clrType = call.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax { TypeArgumentList.Arguments.Count: 1 } generic }
                    ? generic.TypeArgumentList.Arguments[0].ToString()
                    : "string";

                columns.Add(new DiscriminatorColumnConfig(entityName, nameLiteral.Token.ValueText, clrType));

                FluentSyntaxHelpers.WalkChainedTail(call, chained =>
                {
                    if (chained.Expression is not MemberAccessExpressionSyntax
                        {
                            Name: GenericNameSyntax { Identifier.Text: "HasValue", TypeArgumentList.Arguments: [var derivedTypeArg] },
                        })
                    {
                        return;
                    }

                    var valueArg = chained.ArgumentList.Arguments.FirstOrDefault();
                    if (valueArg?.Expression is not LiteralExpressionSyntax)
                    {
                        diagnostics.Add(new Diagnostic(
                            DiagnosticCodes.UnreadableHasValueArgument,
                            "HasValue argument is not a literal and could not be read.",
                            entityName,
                            PropertyName: null,
                            chained.Span));
                        return;
                    }

                    values.Add(new DiscriminatorValueConfig(derivedTypeArg.ToString(), valueArg.Expression.ToString()));
                });
            }
        }

        return new ParseResult<(IReadOnlyList<DiscriminatorColumnConfig>, IReadOnlyList<DiscriminatorValueConfig>)>((columns, values), diagnostics);
    }
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~FluentConfigParserTests"`
Expected: PASS.

- [ ] **Step 7: Run full suite to confirm no regressions**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Parse UseTptMappingStrategy/UseTpcMappingStrategy and HasDiscriminator/HasValue chains"
```

---

### Task 3: `InheritanceInference.Fold` — strategy resolution and TPT partial folding

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Inference/InheritanceInference.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Inference/InheritanceInferenceTests.cs`

**Interfaces:**
- Consumes: `EntityModel.MappingStrategy` (Task 1), `DiagnosticCodes.InconsistentMappingStrategyInHierarchy` (Task 1).
- Produces: `InheritanceFoldResult` gains `IReadOnlyList<Diagnostic> Diagnostics` (new, defaulted to empty — existing two-arg construction in tests keeps compiling); every folded `EntityModel` (including hierarchy roots) has `MappingStrategy` stamped to the hierarchy-wide resolved value.

- [ ] **Step 1: Write the failing tests**

Add to `InheritanceInferenceTests.cs`:

```csharp
    [Fact]
    public void Fold_Tpt_FoldsOnlyInheritedKey_NotOtherAncestorProperties()
    {
        var person = new EntityModel(
            "Person",
            new[] { Property("Id", "int"), Property("Name", "string") },
            KeyPropertyNames: new[] { "Id" },
            MappingStrategy: MappingStrategy.Tpt);
        var student = new EntityModel("Student", new[] { Property("Course", "string") }, BaseEntityName: "Person");

        var result = InheritanceInference.Fold(new[] { person, student });

        var foldedStudent = result.Entities.Single(e => e.Name == "Student");
        Assert.Equal(new[] { "Id", "Course" }, foldedStudent.Properties.Select(p => p.Name));
        Assert.Equal("Person", foldedStudent.Properties.Single(p => p.Name == "Id").DeclaringEntityName);
        Assert.Equal(MappingStrategy.Tpt, foldedStudent.MappingStrategy);
        Assert.Equal(MappingStrategy.Tpt, result.Entities.Single(e => e.Name == "Person").MappingStrategy);
    }

    [Fact]
    public void Fold_Tpc_FoldsAllAncestorProperties_SameAsTph()
    {
        var person = new EntityModel(
            "Person",
            new[] { Property("Id", "int"), Property("Name", "string") },
            KeyPropertyNames: new[] { "Id" },
            MappingStrategy: MappingStrategy.Tpc);
        var student = new EntityModel("Student", new[] { Property("Course", "string") }, BaseEntityName: "Person");

        var result = InheritanceInference.Fold(new[] { person, student });

        var foldedStudent = result.Entities.Single(e => e.Name == "Student");
        Assert.Equal(new[] { "Id", "Name", "Course" }, foldedStudent.Properties.Select(p => p.Name));
        Assert.Equal(MappingStrategy.Tpc, foldedStudent.MappingStrategy);
    }

    [Fact]
    public void Fold_StrategyDeclaredOnDerivedEntityOnly_AppliesToWholeHierarchy()
    {
        var person = new EntityModel("Person", new[] { Property("Id", "int") }, KeyPropertyNames: new[] { "Id" });
        var student = new EntityModel(
            "Student",
            new[] { Property("Course", "string") },
            BaseEntityName: "Person",
            MappingStrategy: MappingStrategy.Tpt);

        var result = InheritanceInference.Fold(new[] { person, student });

        Assert.Equal(MappingStrategy.Tpt, result.Entities.Single(e => e.Name == "Person").MappingStrategy);
        Assert.Equal(MappingStrategy.Tpt, result.Entities.Single(e => e.Name == "Student").MappingStrategy);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Fold_InconsistentStrategyAcrossHierarchy_ResolvesRootPriorityAndEmitsDiagnostic()
    {
        var person = new EntityModel("Person", new[] { Property("Id", "int") }, KeyPropertyNames: new[] { "Id" }, MappingStrategy: MappingStrategy.Tpt);
        var student = new EntityModel(
            "Student",
            new[] { Property("Course", "string") },
            BaseEntityName: "Person",
            MappingStrategy: MappingStrategy.Tpc);

        var result = InheritanceInference.Fold(new[] { person, student });

        Assert.Equal(MappingStrategy.Tpt, result.Entities.Single(e => e.Name == "Person").MappingStrategy);
        Assert.Equal(MappingStrategy.Tpt, result.Entities.Single(e => e.Name == "Student").MappingStrategy);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.InconsistentMappingStrategyInHierarchy, diagnostic.Code);
    }

    [Fact]
    public void Fold_NoEntityHasBaseEntityName_StillReturnsSameReference()
    {
        // Regression guard: a standalone entity (already Tph, the default) must not be copied via
        // `with { }` just because it passed through the strategy-resolution pass.
        var person = new EntityModel("Person", new[] { Property("Id", "int") }, KeyPropertyNames: new[] { "Id" });

        var result = InheritanceInference.Fold(new[] { person });

        Assert.Same(person, Assert.Single(result.Entities));
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~InheritanceInferenceTests"`
Expected: FAIL — `Fold_Tpt_FoldsOnlyInheritedKey_NotOtherAncestorProperties` and `Fold_InconsistentStrategyAcrossHierarchy_ResolvesRootPriorityAndEmitsDiagnostic` fail against current behavior (everything folds like TPH, `Diagnostics` doesn't exist yet).

- [ ] **Step 3: Rewrite `InheritanceInference.cs`**

Replace the file's contents entirely with:

```csharp
using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Model;
using EfSchemaVisualizer.Core.Parsing;
using Microsoft.CodeAnalysis.Text;

namespace EfSchemaVisualizer.Core.Inference;

public sealed record InheritanceFoldResult(
    IReadOnlyList<EntityModel> Entities,
    IReadOnlyList<RelationshipModel> Relationships,
    IReadOnlyList<Diagnostic>? Diagnostics = null)
{
    public IReadOnlyList<Diagnostic> Diagnostics { get; init; } = Diagnostics ?? new List<Diagnostic>();
}

public static class InheritanceInference
{
    public static InheritanceFoldResult Fold(IReadOnlyList<EntityModel> entities)
    {
        var byName = entities.ToDictionary(e => e.Name);
        var rootNameByEntity = entities.ToDictionary(e => e.Name, e => ResolveRootName(e, byName));
        var (resolvedStrategy, diagnostics) = ResolveMappingStrategies(entities, rootNameByEntity);

        var foldedEntities = new List<EntityModel>();
        var relationships = new List<RelationshipModel>();

        foreach (var entity in entities)
        {
            var strategy = resolvedStrategy[rootNameByEntity[entity.Name]];

            if (entity.BaseEntityName is null || !byName.ContainsKey(entity.BaseEntityName))
            {
                foldedEntities.Add(strategy == entity.MappingStrategy ? entity : entity with { MappingStrategy = strategy });
                continue;
            }

            var nearestFirstChain = BuildAncestorChain(entity, byName);

            var keyPropertyNames = entity.KeyPropertyNames;
            var isKeyInferred = entity.IsKeyInferred;
            if (keyPropertyNames.Count == 0 && !entity.IsKeyless)
            {
                var nearestKeyedAncestor = nearestFirstChain.FirstOrDefault(a => a.KeyPropertyNames.Count > 0);
                if (nearestKeyedAncestor is not null)
                {
                    keyPropertyNames = nearestKeyedAncestor.KeyPropertyNames;
                    isKeyInferred = true;
                }
            }

            var ownNames = new HashSet<string>(entity.Properties.Select(p => p.Name));
            var foldedProperties = new List<PropertyModel>();

            if (strategy == MappingStrategy.Tpt)
            {
                // TPT: the derived table physically has only its own columns plus the shared
                // PK/FK back to the base table, so fold in just the (possibly-inherited) key
                // property/properties — not the rest of the ancestor's columns.
                foreach (var name in keyPropertyNames)
                {
                    if (ownNames.Contains(name))
                    {
                        continue;
                    }

                    var match = nearestFirstChain
                        .Select(a => (Property: a.Properties.FirstOrDefault(p => p.Name == name), Owner: a))
                        .FirstOrDefault(x => x.Property is not null);

                    if (match.Property is not null)
                    {
                        foldedProperties.Add(match.Property with { DeclaringEntityName = match.Owner.Name });
                    }
                }
            }
            else
            {
                // TPH / TPC: fold every ancestor property into one flat shape (today's behavior).
                // Root-first pass: decide the ORDER ancestor property names first appear in.
                var seenNames = new HashSet<string>(ownNames);
                var ancestorPropertyNamesInOrder = new List<string>();

                foreach (var ancestor in nearestFirstChain.AsEnumerable().Reverse())
                {
                    foreach (var property in ancestor.Properties)
                    {
                        if (seenNames.Add(property.Name))
                        {
                            ancestorPropertyNamesInOrder.Add(property.Name);
                        }
                    }
                }

                // Nearest-first pass: for each name, the NEAREST ancestor that declares it wins
                // (shadowing), even though the further ancestor may have declared it first.
                foreach (var name in ancestorPropertyNamesInOrder)
                {
                    var (winningProperty, owner) = nearestFirstChain
                        .Select(a => (Property: a.Properties.FirstOrDefault(p => p.Name == name), Owner: a))
                        .First(x => x.Property is not null);

                    foldedProperties.Add(winningProperty! with { DeclaringEntityName = owner.Name });
                }
            }

            foldedProperties.AddRange(entity.Properties);

            foldedEntities.Add(entity with
            {
                Properties = foldedProperties,
                KeyPropertyNames = keyPropertyNames,
                IsKeyInferred = isKeyInferred,
                MappingStrategy = strategy,
            });

            var directBase = byName[entity.BaseEntityName];
            relationships.Add(new RelationshipModel(
                directBase.Name,
                entity.Name,
                RelationshipKind.Inheritance,
                PrincipalNavigation: null,
                DependentNavigation: null,
                ForeignKeyProperties: new List<string>(),
                IsInferred: false));
        }

        return new InheritanceFoldResult(foldedEntities, relationships, diagnostics);
    }

    /// Resolves one mapping strategy per hierarchy (grouped by root entity name): the root's own
    /// explicit strategy wins if it has one; otherwise the first explicit strategy found among its
    /// descendants (in list order) wins; a hierarchy with no explicit strategy anywhere defaults to
    /// TPH. If more than one DISTINCT explicit strategy is declared across a hierarchy's members,
    /// the resolution above still picks one (root-priority) but an `InconsistentMappingStrategyInHierarchy`
    /// diagnostic is emitted, since that combination is invalid at EF's own model-build time.
    private static (Dictionary<string, MappingStrategy> Resolved, List<Diagnostic> Diagnostics) ResolveMappingStrategies(
        IReadOnlyList<EntityModel> entities, Dictionary<string, string> rootNameByEntity)
    {
        var resolved = new Dictionary<string, MappingStrategy>();
        var diagnostics = new List<Diagnostic>();

        var membersByRoot = entities
            .GroupBy(e => rootNameByEntity[e.Name])
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var (rootName, members) in membersByRoot)
        {
            var ordered = members.OrderBy(m => m.Name == rootName ? 0 : 1).ToList();

            var distinctExplicit = ordered
                .Select(m => m.MappingStrategy)
                .Where(s => s != MappingStrategy.Tph)
                .Distinct()
                .ToList();

            resolved[rootName] = distinctExplicit.FirstOrDefault();

            if (distinctExplicit.Count > 1)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticCodes.InconsistentMappingStrategyInHierarchy,
                    $"Entities in the '{rootName}' hierarchy declare more than one mapping strategy; using '{resolved[rootName]}'.",
                    rootName,
                    PropertyName: null,
                    TextSpan.FromBounds(0, 0),
                    DiagnosticCategory.ModelValidity));
            }
        }

        return (resolved, diagnostics);
    }

    /// The topmost ancestor name reachable from `entity` (cycle-guarded via `BuildAncestorChain`),
    /// or `entity`'s own name if it has no resolvable base.
    private static string ResolveRootName(EntityModel entity, Dictionary<string, EntityModel> byName)
    {
        if (entity.BaseEntityName is null || !byName.ContainsKey(entity.BaseEntityName))
        {
            return entity.Name;
        }

        var chain = BuildAncestorChain(entity, byName);
        return chain.Count > 0 ? chain[^1].Name : entity.Name;
    }

    /// Nearest-ancestor-first (immediate parent, grandparent, ...). Cycle-guarded: a
    /// malformed `BaseEntityName` loop stops instead of looping forever.
    private static List<EntityModel> BuildAncestorChain(
        EntityModel entity, Dictionary<string, EntityModel> byName)
    {
        var chain = new List<EntityModel>();
        var visited = new HashSet<string> { entity.Name };
        var current = entity;

        while (current.BaseEntityName is not null && byName.TryGetValue(current.BaseEntityName, out var ancestor))
        {
            if (!visited.Add(ancestor.Name))
            {
                break;
            }

            chain.Add(ancestor);
            current = ancestor;
        }

        return chain;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~InheritanceInferenceTests"`
Expected: PASS — all existing tests (unchanged logic for TPH) plus the five new ones.

- [ ] **Step 5: Run full suite to confirm no regressions**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Inference/InheritanceInference.cs tests/EfSchemaVisualizer.Core.Tests/Inference/InheritanceInferenceTests.cs
git commit -m "Resolve mapping strategy per hierarchy and branch TPT/TPC folding in InheritanceInference"
```

---

### Task 4: Wire parsing/merging into `DiagramModelBuilder`

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs`

**Interfaces:**
- Consumes: `FluentConfigParser.ParseMappingStrategies`/`ParseDiscriminators` (Task 2), `ModelMerger.ApplyMappingStrategies`/`ApplyDiscriminatorColumn`/`ApplyDiscriminatorValue` (Task 1), `InheritanceInference.Fold` returning `Diagnostics` (Task 3).
- Produces: end-to-end — `DiagramModelBuilder.Build` output now reflects mapping strategy and discriminator config from raw source.

- [ ] **Step 1: Write the failing tests**

Add to `DiagramModelBuilderTests.cs`:

```csharp
    [Fact]
    public void Build_TptHierarchy_DerivedEntityShowsOnlyOwnAndKeyColumns_EdgeStillPresent()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }
                public string Name { get; set; } = null!;
            }

            public class Student : Person
            {
                public string Course { get; set; } = null!;
            }
            """;
        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.HasKey(e => e.Id);
                        entity.UseTptMappingStrategy();
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var student = result.Entities.Single(e => e.Name == "Student");
        Assert.Equal(new[] { "Id", "Course" }, student.Properties.Select(p => p.Name));
        Assert.Equal(MappingStrategy.Tpt, student.MappingStrategy);
        Assert.Equal(MappingStrategy.Tpt, result.Entities.Single(e => e.Name == "Person").MappingStrategy);

        Assert.Contains(result.Relationships, r => r.Kind == RelationshipKind.Inheritance && r.DependentEntity == "Student");
    }

    [Fact]
    public void Build_TpcHierarchy_DerivedEntityFullyFolded_NoInheritanceDiagnosticNoise()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }
                public string Name { get; set; } = null!;
            }

            public class Student : Person
            {
                public string Course { get; set; } = null!;
            }
            """;
        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.HasKey(e => e.Id);
                        entity.UseTpcMappingStrategy();
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var student = result.Entities.Single(e => e.Name == "Student");
        Assert.Equal(new[] { "Id", "Name", "Course" }, student.Properties.Select(p => p.Name));
        Assert.Equal(MappingStrategy.Tpc, student.MappingStrategy);
    }

    [Fact]
    public void Build_TphHierarchyWithDiscriminator_ParsesColumnAndValuesOntoEntities()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }
            }

            public class Student : Person
            {
                public string Course { get; set; } = null!;
            }

            public class Teacher : Person
            {
                public string Salary { get; set; } = null!;
            }
            """;
        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.HasKey(e => e.Id);
                        entity.HasDiscriminator<string>("Type").HasValue<Student>("S").HasValue<Teacher>("T");
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var person = result.Entities.Single(e => e.Name == "Person");
        Assert.Equal("Type", person.DiscriminatorPropertyName);
        Assert.Equal("string", person.DiscriminatorClrType);
        Assert.Equal(MappingStrategy.Tph, person.MappingStrategy);

        Assert.Equal("\"S\"", result.Entities.Single(e => e.Name == "Student").DiscriminatorValue);
        Assert.Equal("\"T\"", result.Entities.Single(e => e.Name == "Teacher").DiscriminatorValue);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~DiagramModelBuilderTests"`
Expected: FAIL — `student.MappingStrategy`/`person.DiscriminatorPropertyName` etc. are still all default (Tph/null), since the builder doesn't parse/merge these yet.

- [ ] **Step 3: Wire the new parse calls into `DiagramModelBuilder.cs`**

After `var useSequences = configParser.ParseUseSequences(configSource);` (line 63), add:

```csharp
        var mappingStrategies = configParser.ParseMappingStrategies(configSource);
        var discriminators = configParser.ParseDiscriminators(configSource);
```

After `diagnostics.AddRange(useSequences.Diagnostics);` (line 99), add:

```csharp
        diagnostics.AddRange(discriminators.Diagnostics);
```

(`mappingStrategies` never produces diagnostics — no `AddRange` needed for it.)

In the `mergedEntities` `.Select(...)` chain, after `.Select(entity => ModelMerger.ApplyUseSequences(entity, useSequences.Value))` (line 138), add:

```csharp
            .Select(entity => ModelMerger.ApplyMappingStrategies(entity, mappingStrategies.Value))
            .Select(entity => ModelMerger.ApplyDiscriminatorColumn(entity, discriminators.Value.Columns))
            .Select(entity => ModelMerger.ApplyDiscriminatorValue(entity, discriminators.Value.Values))
```

Right after `var inheritanceFold = InheritanceInference.Fold(entities);` (line 153), add:

```csharp
        diagnostics.AddRange(inheritanceFold.Diagnostics);
```

(keep the existing `entities = inheritanceFold.Entities;` line immediately after, unchanged).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~DiagramModelBuilderTests"`
Expected: PASS.

- [ ] **Step 5: Run full suite to confirm no regressions**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs
git commit -m "Wire mapping-strategy and discriminator parsing into DiagramModelBuilder pipeline"
```

---

### Task 5: `OnModelCreatingRewriter` — mapping strategy set/remove

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `MappingStrategy` (Task 1), `FindConfigScopes`, `GetScopeBlockAndReceiver`, `FindOnModelCreatingMethod`, `BuildEntityInvocationStatement` (existing private helpers in the same class).
- Produces: `OnModelCreatingRewriter.SetMappingStrategy(string sourceCode, string entityName, MappingStrategy strategy) -> string`; `OnModelCreatingRewriter.RemoveMappingStrategy(string sourceCode, string entityName) -> string`.

- [ ] **Step 1: Write the failing tests**

Add to `OnModelCreatingRewriterTests.cs`:

```csharp
    private const string InheritanceSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.HasKey(e => e.Id);
                });
            }
        }
        """;

    [Fact]
    public void SetMappingStrategy_Tpt_InsertsUseTptMappingStrategyCall()
    {
        var result = new OnModelCreatingRewriter().SetMappingStrategy(InheritanceSource, "Person", MappingStrategy.Tpt);

        Assert.Contains("entity.UseTptMappingStrategy();", result);
    }

    [Fact]
    public void SetMappingStrategy_SwitchingFromTptToTpc_ReplacesTheCall()
    {
        var withTpt = new OnModelCreatingRewriter().SetMappingStrategy(InheritanceSource, "Person", MappingStrategy.Tpt);

        var result = new OnModelCreatingRewriter().SetMappingStrategy(withTpt, "Person", MappingStrategy.Tpc);

        Assert.DoesNotContain("UseTptMappingStrategy", result);
        Assert.Contains("entity.UseTpcMappingStrategy();", result);
    }

    [Fact]
    public void SetMappingStrategy_Tph_RemovesAnyExistingStrategyCall()
    {
        var withTpc = new OnModelCreatingRewriter().SetMappingStrategy(InheritanceSource, "Person", MappingStrategy.Tpc);

        var result = new OnModelCreatingRewriter().SetMappingStrategy(withTpc, "Person", MappingStrategy.Tph);

        Assert.DoesNotContain("MappingStrategy", result);
    }

    [Fact]
    public void SetMappingStrategy_NoExistingScope_SynthesizesEntityBlock()
    {
        var result = new OnModelCreatingRewriter().SetMappingStrategy(Source, "Order", MappingStrategy.Tpc);

        Assert.Contains("modelBuilder.Entity<Order>(entity =>", result);
        Assert.Contains("entity.UseTpcMappingStrategy();", result);

        // Untouched: Person's existing config.
        Assert.Contains("entity.Property(e => e.Name).HasMaxLength(100);", result);
    }

    [Fact]
    public void RemoveMappingStrategy_NoExistingCall_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter().RemoveMappingStrategy(InheritanceSource, "Person");

        Assert.Equal(InheritanceSource, result);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: FAIL to compile — `SetMappingStrategy`/`RemoveMappingStrategy` don't exist yet.

- [ ] **Step 3: Implement the two methods**

Add near `SetKeyless`/`RemoveKeyless`:

```csharp
    public string SetMappingStrategy(string sourceCode, string entityName, MappingStrategy strategy)
    {
        var withoutExisting = RemoveMappingStrategy(sourceCode, entityName);

        if (strategy == MappingStrategy.Tph)
        {
            return withoutExisting;
        }

        var tree = CSharpSyntaxTree.ParseText(withoutExisting);
        var root = tree.GetCompilationUnitRoot();
        var methodName = strategy == MappingStrategy.Tpt ? "UseTptMappingStrategy" : "UseTpcMappingStrategy";

        var scopes = FindConfigScopes(root, entityName);
        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            var (block, blockReceiverName) = GetScopeBlockAndReceiver(existingScope);
            var newStatement = BuildBareEntityCallStatement(blockReceiverName, methodName);
            var newBlock = block.AddStatements(newStatement);

            var newRoot = root.ReplaceNode(block, newBlock);
            return newRoot.NormalizeWhitespace().ToFullString();
        }

        var method = FindOnModelCreatingMethod(root);
        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;
        var statement = BuildBareEntityCallStatement("entity", methodName);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(statement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot2 = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot2.NormalizeWhitespace().ToFullString();
    }

    public string RemoveMappingStrategy(string sourceCode, string entityName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);
        var existingCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "UseTptMappingStrategy")
                .Concat(FluentSyntaxHelpers.FindCallsNamed(scope, "UseTpcMappingStrategy")))
            .FirstOrDefault();

        if (existingCall is null || existingCall.Parent is not ExpressionStatementSyntax statement)
        {
            return sourceCode;
        }

        var newRoot = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!;
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildBareEntityCallStatement(string blockReceiverName, string methodName)
    {
        return SyntaxFactory.ExpressionStatement(
            SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName(blockReceiverName),
                    SyntaxFactory.IdentifierName(methodName)),
                SyntaxFactory.ArgumentList()));
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: PASS.

- [ ] **Step 5: Run full suite to confirm no regressions**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Add OnModelCreatingRewriter.SetMappingStrategy/RemoveMappingStrategy"
```

---

### Task 6: `OnModelCreatingRewriter` — discriminator set/remove

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`

**Interfaces:**
- Consumes: `FindOutermostChainedCall` (existing private helper in the same class, used by `RemoveSequence`), `FindConfigScopes`, `GetScopeBlockAndReceiver`, `BuildEntityInvocationStatement`, `FindOnModelCreatingMethod`.
- Produces: `OnModelCreatingRewriter.SetDiscriminator(string sourceCode, string rootEntityName, string columnName, string clrType, IReadOnlyList<(string DerivedEntityName, string Value)> values) -> string`; `OnModelCreatingRewriter.RemoveDiscriminator(string sourceCode, string rootEntityName) -> string`.

- [ ] **Step 1: Write the failing tests**

Add to `OnModelCreatingRewriterTests.cs`:

```csharp
    [Fact]
    public void SetDiscriminator_NoExistingCall_InsertsFullChain()
    {
        var result = new OnModelCreatingRewriter().SetDiscriminator(
            InheritanceSource, "Person", "Type", "string",
            new[] { ("Student", "\"S\""), ("Teacher", "\"T\"") });

        Assert.Contains("entity.HasDiscriminator<string>(\"Type\").HasValue<Student>(\"S\").HasValue<Teacher>(\"T\");", result);
    }

    [Fact]
    public void SetDiscriminator_ExistingChain_IsFullyReplaced()
    {
        var withOneValue = new OnModelCreatingRewriter().SetDiscriminator(
            InheritanceSource, "Person", "Type", "string", new[] { ("Student", "\"S\"") });

        var result = new OnModelCreatingRewriter().SetDiscriminator(
            withOneValue, "Person", "Type", "string",
            new[] { ("Student", "\"S\""), ("Teacher", "\"T\"") });

        Assert.Contains("entity.HasDiscriminator<string>(\"Type\").HasValue<Student>(\"S\").HasValue<Teacher>(\"T\");", result);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result, "HasDiscriminator"));
    }

    [Fact]
    public void SetDiscriminator_EmptyValueList_WritesColumnOnlyWithNoHasValueChain()
    {
        var result = new OnModelCreatingRewriter().SetDiscriminator(
            InheritanceSource, "Person", "Type", "string", System.Array.Empty<(string, string)>());

        Assert.Contains("entity.HasDiscriminator<string>(\"Type\");", result);
    }

    [Fact]
    public void RemoveDiscriminator_RemovesEntireChain()
    {
        var withDiscriminator = new OnModelCreatingRewriter().SetDiscriminator(
            InheritanceSource, "Person", "Type", "string",
            new[] { ("Student", "\"S\"") });

        var result = new OnModelCreatingRewriter().RemoveDiscriminator(withDiscriminator, "Person");

        Assert.DoesNotContain("HasDiscriminator", result);
        Assert.DoesNotContain("HasValue", result);
    }

    [Fact]
    public void RemoveDiscriminator_NoExistingCall_ReturnsSourceUnchanged()
    {
        var result = new OnModelCreatingRewriter().RemoveDiscriminator(InheritanceSource, "Person");

        Assert.Equal(InheritanceSource, result);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: FAIL to compile — `SetDiscriminator`/`RemoveDiscriminator` don't exist yet.

- [ ] **Step 3: Implement the two methods**

Add near `SetSequence`/`RemoveSequence`:

```csharp
    public string SetDiscriminator(
        string sourceCode, string rootEntityName, string columnName, string clrType,
        IReadOnlyList<(string DerivedEntityName, string Value)> values)
    {
        var withoutExisting = RemoveDiscriminator(sourceCode, rootEntityName);

        var tree = CSharpSyntaxTree.ParseText(withoutExisting);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, rootEntityName);
        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            var (block, blockReceiverName) = GetScopeBlockAndReceiver(existingScope);
            var statement = SyntaxFactory.ExpressionStatement(BuildDiscriminatorExpression(blockReceiverName, columnName, clrType, values));
            var newBlock = block.AddStatements(statement);

            var newRoot = root.ReplaceNode(block, newBlock);
            return newRoot.NormalizeWhitespace().ToFullString();
        }

        var method = FindOnModelCreatingMethod(root);
        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;
        var entityStatement = SyntaxFactory.ExpressionStatement(BuildDiscriminatorExpression("entity", columnName, clrType, values));
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, rootEntityName, SyntaxFactory.Block(entityStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot2 = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot2.NormalizeWhitespace().ToFullString();
    }

    public string RemoveDiscriminator(string sourceCode, string rootEntityName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, rootEntityName);
        var existingCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasDiscriminator"))
            .FirstOrDefault();

        if (existingCall is null)
        {
            return sourceCode;
        }

        var outermostChainedCall = FindOutermostChainedCall(existingCall);
        var statement = outermostChainedCall.Ancestors().OfType<ExpressionStatementSyntax>().First();

        var newRoot = root.RemoveNode(statement, SyntaxRemoveOptions.KeepNoTrivia)!;
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionSyntax BuildDiscriminatorExpression(
        string receiverName, string columnName, string clrType,
        IReadOnlyList<(string DerivedEntityName, string Value)> values)
    {
        SimpleNameSyntax hasDiscriminatorName = SyntaxFactory.GenericName(SyntaxFactory.Identifier("HasDiscriminator"))
            .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList<TypeSyntax>(SyntaxFactory.ParseTypeName(clrType))));

        ExpressionSyntax expression = SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, SyntaxFactory.IdentifierName(receiverName), hasDiscriminatorName),
            SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                SyntaxFactory.Argument(SyntaxFactory.LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(columnName))))));

        foreach (var (derivedEntityName, value) in values)
        {
            var hasValueName = SyntaxFactory.GenericName(SyntaxFactory.Identifier("HasValue"))
                .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList<TypeSyntax>(SyntaxFactory.ParseTypeName(derivedEntityName))));

            expression = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, expression, hasValueName),
                SyntaxFactory.ArgumentList(SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(SyntaxFactory.ParseExpression(value)))));
        }

        return expression;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~OnModelCreatingRewriterTests"`
Expected: PASS.

- [ ] **Step 5: Run full suite to confirm no regressions**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs
git commit -m "Add OnModelCreatingRewriter.SetDiscriminator/RemoveDiscriminator"
```

---

### Task 7: `DiagramEditor` — mapping strategy and discriminator edit methods

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorInheritanceTests.cs`

**Interfaces:**
- Consumes: `OnModelCreatingRewriter.SetMappingStrategy`/`RemoveMappingStrategy` (Task 5), `SetDiscriminator`/`RemoveDiscriminator` (Task 6), `DiagramEditResult.Ok()`/`Fail(string)` (existing), `FormatDefaultValueLiteral` (existing private static helper in the same class).
- Produces: `DiagramEditor.SetMappingStrategy(string entityName, MappingStrategy strategy) -> DiagramEditResult`; `DiagramEditor.SetDiscriminatorColumn(string rootEntityName, string columnName, string? clrTypeName) -> DiagramEditResult`; `DiagramEditor.RemoveDiscriminatorColumn(string rootEntityName) -> DiagramEditResult`; `DiagramEditor.SetDiscriminatorValue(string derivedEntityName, string? value) -> DiagramEditResult`; `DiagramEditor.RemoveDiscriminatorValue(string derivedEntityName) -> DiagramEditResult`.

- [ ] **Step 1: Write the failing tests**

Add to `DiagramEditorInheritanceTests.cs` (check the file's existing helper method names — e.g. a shared `CreateEditor(classSource, configSource)` — and reuse them rather than inlining `new DiagramEditor(...)`):

```csharp
    private const string TptClassSource = """
        public class Person
        {
            public int Id { get; set; }
            public string Name { get; set; } = null!;
        }

        public class Student : Person
        {
            public string Course { get; set; } = null!;
        }
        """;
    private const string TptConfigSource = """
        public class AppDbContext : DbContext
        {
            protected override void OnModelCreating(ModelBuilder modelBuilder)
            {
                modelBuilder.Entity<Person>(entity =>
                {
                    entity.HasKey(e => e.Id);
                });
            }
        }
        """;

    [Fact]
    public void SetMappingStrategy_Tpt_UpdatesEveryEntityInHierarchy()
    {
        var editor = new DiagramEditor(TptClassSource, TptConfigSource);

        var result = editor.SetMappingStrategy("Student", MappingStrategy.Tpt);

        Assert.True(result.Success);
        Assert.Equal(MappingStrategy.Tpt, editor.Current.Entities.Single(e => e.Name == "Person").MappingStrategy);
        Assert.Equal(MappingStrategy.Tpt, editor.Current.Entities.Single(e => e.Name == "Student").MappingStrategy);
    }

    [Fact]
    public void SetMappingStrategy_BlockedWhenDiscriminatorConfigured()
    {
        const string configWithDiscriminator = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.HasKey(e => e.Id);
                        entity.HasDiscriminator<string>("Type").HasValue<Student>("S");
                    });
                }
            }
            """;
        var editor = new DiagramEditor(TptClassSource, configWithDiscriminator);

        var result = editor.SetMappingStrategy("Person", MappingStrategy.Tpt);

        Assert.False(result.Success);
        Assert.Equal(MappingStrategy.Tph, editor.Current.Entities.Single(e => e.Name == "Person").MappingStrategy);
    }

    [Fact]
    public void SetDiscriminatorColumn_ThenSetDiscriminatorValue_RoundTrips()
    {
        var editor = new DiagramEditor(TptClassSource, TptConfigSource);

        var columnResult = editor.SetDiscriminatorColumn("Person", "Type", null);
        Assert.True(columnResult.Success);
        Assert.Equal("Type", editor.Current.Entities.Single(e => e.Name == "Person").DiscriminatorPropertyName);
        Assert.Equal("string", editor.Current.Entities.Single(e => e.Name == "Person").DiscriminatorClrType);

        var valueResult = editor.SetDiscriminatorValue("Student", "S");
        Assert.True(valueResult.Success);
        Assert.Equal("\"S\"", editor.Current.Entities.Single(e => e.Name == "Student").DiscriminatorValue);
    }

    [Fact]
    public void SetDiscriminatorColumn_BlockedWhenStrategyIsNotTph()
    {
        var editor = new DiagramEditor(TptClassSource, TptConfigSource);
        editor.SetMappingStrategy("Person", MappingStrategy.Tpt);

        var result = editor.SetDiscriminatorColumn("Person", "Type", null);

        Assert.False(result.Success);
    }

    [Fact]
    public void SetDiscriminatorValue_NoColumnConfiguredYet_Fails()
    {
        var editor = new DiagramEditor(TptClassSource, TptConfigSource);

        var result = editor.SetDiscriminatorValue("Student", "S");

        Assert.False(result.Success);
    }

    [Fact]
    public void RemoveDiscriminatorValue_ClearsJustThatEntity_LeavesOthersIntact()
    {
        const string configWithTwoValues = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.HasKey(e => e.Id);
                        entity.HasDiscriminator<string>("Type").HasValue<Student>("S").HasValue<Teacher>("T");
                    });
                }
            }
            """;
        const string threeLevelClassSource = """
            public class Person
            {
                public int Id { get; set; }
            }

            public class Student : Person
            {
                public string Course { get; set; } = null!;
            }

            public class Teacher : Person
            {
                public string Salary { get; set; } = null!;
            }
            """;
        var editor = new DiagramEditor(threeLevelClassSource, configWithTwoValues);

        var result = editor.RemoveDiscriminatorValue("Student");

        Assert.True(result.Success);
        Assert.Null(editor.Current.Entities.Single(e => e.Name == "Student").DiscriminatorValue);
        Assert.Equal("\"T\"", editor.Current.Entities.Single(e => e.Name == "Teacher").DiscriminatorValue);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~DiagramEditorInheritanceTests"`
Expected: FAIL to compile — the five new `DiagramEditor` methods don't exist yet.

- [ ] **Step 3: Implement the methods**

Add near `ResolveDeclaringEntity`:

```csharp
    private string ResolveHierarchyRoot(string entityName)
    {
        var byName = Current.Entities.ToDictionary(e => e.Name);
        var visited = new HashSet<string> { entityName };
        var current = entityName;

        while (byName.TryGetValue(current, out var entity)
            && entity.BaseEntityName is not null
            && byName.ContainsKey(entity.BaseEntityName))
        {
            if (!visited.Add(entity.BaseEntityName))
            {
                break;
            }

            current = entity.BaseEntityName;
        }

        return current;
    }

    public DiagramEditResult SetMappingStrategy(string entityName, MappingStrategy strategy)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        if (strategy == entity.MappingStrategy)
        {
            return DiagramEditResult.Ok();
        }

        var rootName = ResolveHierarchyRoot(entityName);

        if (strategy != MappingStrategy.Tph)
        {
            var conflicting = Current.Entities
                .Where(e => ResolveHierarchyRoot(e.Name) == rootName)
                .FirstOrDefault(e => e.DiscriminatorPropertyName is not null || e.DiscriminatorValue is not null);

            if (conflicting is not null)
            {
                return DiagramEditResult.Fail(
                    $"Cannot switch '{rootName}' to {strategy} while discriminator configuration exists on '{conflicting.Name}'. Remove the discriminator configuration first.");
            }
        }

        var newConfigSource = _configRewriter.SetMappingStrategy(ConfigSource, rootName, strategy);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetDiscriminatorColumn(string rootEntityName, string columnName, string? clrTypeName)
    {
        var root = Current.Entities.FirstOrDefault(e => e.Name == rootEntityName);
        if (root is null)
        {
            return DiagramEditResult.Fail($"Entity '{rootEntityName}' not found.");
        }

        if (string.IsNullOrWhiteSpace(columnName))
        {
            return DiagramEditResult.Fail("Discriminator column name cannot be empty.");
        }

        if (root.MappingStrategy != MappingStrategy.Tph)
        {
            return DiagramEditResult.Fail($"Cannot configure a discriminator on '{rootEntityName}' while its mapping strategy is {root.MappingStrategy}.");
        }

        var clrType = string.IsNullOrWhiteSpace(clrTypeName) ? "string" : clrTypeName.Trim();

        var existingValues = Current.Entities
            .Where(e => e.BaseEntityName is not null && ResolveHierarchyRoot(e.Name) == rootEntityName && e.DiscriminatorValue is not null)
            .Select(e => (e.Name, e.DiscriminatorValue!))
            .ToList();

        var newConfigSource = _configRewriter.SetDiscriminator(ConfigSource, rootEntityName, columnName.Trim(), clrType, existingValues);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult RemoveDiscriminatorColumn(string rootEntityName)
    {
        var root = Current.Entities.FirstOrDefault(e => e.Name == rootEntityName);
        if (root is null)
        {
            return DiagramEditResult.Fail($"Entity '{rootEntityName}' not found.");
        }

        var newConfigSource = _configRewriter.RemoveDiscriminator(ConfigSource, rootEntityName);
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult SetDiscriminatorValue(string derivedEntityName, string? value)
    {
        var derived = Current.Entities.FirstOrDefault(e => e.Name == derivedEntityName);
        if (derived is null)
        {
            return DiagramEditResult.Fail($"Entity '{derivedEntityName}' not found.");
        }

        var rootName = ResolveHierarchyRoot(derivedEntityName);
        var root = Current.Entities.FirstOrDefault(e => e.Name == rootName);

        if (root?.DiscriminatorPropertyName is null)
        {
            return DiagramEditResult.Fail($"'{rootName}' has no discriminator column configured yet.");
        }

        if (root.MappingStrategy != MappingStrategy.Tph)
        {
            return DiagramEditResult.Fail($"Cannot configure a discriminator value while '{rootName}'s mapping strategy is {root.MappingStrategy}.");
        }

        var trimmedInput = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        string? normalizedValue = null;
        if (trimmedInput is not null)
        {
            normalizedValue = FormatDefaultValueLiteral(trimmedInput, root.DiscriminatorClrType ?? "string");
            if (normalizedValue is null)
            {
                return DiagramEditResult.Fail($"'{trimmedInput}' is not a valid discriminator value for '{derivedEntityName}'.");
            }
        }

        var values = Current.Entities
            .Where(e => e.BaseEntityName is not null && ResolveHierarchyRoot(e.Name) == rootName && e.DiscriminatorValue is not null)
            .ToDictionary(e => e.Name, e => e.DiscriminatorValue!);

        if (normalizedValue is null)
        {
            values.Remove(derivedEntityName);
        }
        else
        {
            values[derivedEntityName] = normalizedValue;
        }

        var newConfigSource = _configRewriter.SetDiscriminator(
            ConfigSource, rootName, root.DiscriminatorPropertyName, root.DiscriminatorClrType ?? "string",
            values.Select(kv => (kv.Key, kv.Value)).ToList());
        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }

    public DiagramEditResult RemoveDiscriminatorValue(string derivedEntityName) => SetDiscriminatorValue(derivedEntityName, null);
```

Note: `FormatDefaultValueLiteral` is `private static string?` and already takes `(string rawText, string clrType)` — verify its exact signature in `DiagramEditor.cs` before wiring the call above; adjust the call site if the parameter order differs.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~DiagramEditorInheritanceTests"`
Expected: PASS.

- [ ] **Step 5: Run full suite to confirm no regressions**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorInheritanceTests.cs
git commit -m "Add DiagramEditor mapping-strategy and discriminator edit methods"
```

---

### Task 8: Rendering — TPC edge suppression and `EntityNode.razor` UI

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramSync.cs`
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`
- Test: locate and check the existing markup-source `SafeEdit`-coverage test (referenced in backlog as `GestureHandlerSafeEditTests`) — find it with `grep -rl GestureHandlerSafeEditTests tests/` — and a manual/dev-server smoke check.

**Interfaces:**
- Consumes: `EntityModel.MappingStrategy`/`DiscriminatorPropertyName`/`DiscriminatorClrType`/`DiscriminatorValue` (Task 1), `DiagramEditor.SetMappingStrategy`/`SetDiscriminatorColumn`/`RemoveDiscriminatorColumn`/`SetDiscriminatorValue` (Task 7), existing `SafeEdit` helper and `EditContext.NotifyChangedAsync()` pattern already used throughout `EntityNode.razor`.
- Produces: no new public interfaces — UI-only.

- [ ] **Step 1: Write the failing test**

`DiagramSync.cs` has no dedicated test file today (verify with `grep -rl "class DiagramSync" tests/` — if a `DiagramSyncTests.cs` exists, add to it; otherwise this step's coverage comes from the manual smoke check in Step 5, since `DiagramSync` operates on `Blazor.Diagrams` UI objects that aren't easily unit-testable in isolation — note this explicitly rather than inventing a brittle test).

If a test file exists, add:

```csharp
    [Fact]
    public void Rebuild_TpcInheritanceRelationship_DoesNotAddLink()
    {
        var person = new EntityModel("Person", new List<PropertyModel>(), KeyPropertyNames: new[] { "Id" });
        var student = new EntityModel("Student", new List<PropertyModel>(), MappingStrategy: MappingStrategy.Tpc);
        var relationships = new[] { new RelationshipModel("Person", "Student", RelationshipKind.Inheritance, null, null) };
        var result = new DiagramModelResult(new[] { person, student }, relationships, Array.Empty<Diagnostic>(), Array.Empty<SequenceModel>());

        var diagram = new BlazorDiagram();
        var entityIds = new Dictionary<string, Guid> { ["Person"] = Guid.NewGuid(), ["Student"] = Guid.NewGuid() };

        DiagramSync.Rebuild(diagram, result, entityIds);

        Assert.Empty(diagram.Links);
    }
```

- [ ] **Step 2: Run test to verify it fails (if applicable)**

Run: `dotnet test --filter "FullyQualifiedName~DiagramSync"`
Expected: FAIL — a link is currently added regardless of `MappingStrategy`.

- [ ] **Step 3: Update `DiagramSync.cs`**

Replace the relationship loop body:

```csharp
        foreach (var relationship in result.Relationships)
        {
            if (!nodesByEntityName.TryGetValue(relationship.PrincipalEntity, out var principalNode) ||
                !nodesByEntityName.TryGetValue(relationship.DependentEntity, out var dependentNode))
            {
                continue;
            }

            if (relationship.Kind == RelationshipKind.Inheritance)
            {
                var dependentEntity = result.Entities.FirstOrDefault(e => e.Name == relationship.DependentEntity);
                if (dependentEntity?.MappingStrategy == MappingStrategy.Tpc)
                {
                    continue;
                }
            }

            var link = new LinkModel(dependentNode, principalNode);
            if (relationship.Kind == RelationshipKind.Inheritance)
            {
                link.Color = "#4a5a8a";
            }
            else if (relationship.Kind == RelationshipKind.Owned)
            {
                link.Color = "#8a6a4a";
            }
            else if (relationship.IsInferred)
            {
                link.Color = "#aaaaaa";
            }
            link.Labels.Add(new RelationshipLinkLabelModel(link, relationship));
            diagram.Links.Add(link);
        }
```

- [ ] **Step 4: Add the mapping-strategy dropdown and discriminator panel to `EntityNode.razor`**

Insert after the "PK name" block (after the `_keyNameError` conditional, before the SQL query block):

```razor
    @{
        var isHierarchyRoot = Node.Entity.BaseEntityName is null
            && EditContext.Editor.Current.Entities.Any(e => e.BaseEntityName == Node.Entity.Name);
        var isInHierarchy = Node.Entity.BaseEntityName is not null || isHierarchyRoot;
    }
    @if (isInHierarchy)
    {
        <div style="padding: 2px 8px; font-size: 0.75em; color: #555; display: flex; align-items: center; gap: 4px;">
            <span title="Inheritance mapping strategy (applies to the whole hierarchy).">Mapping:</span>
            <select value="@Node.Entity.MappingStrategy" @onchange="e => CommitMappingStrategy(e.Value?.ToString())"
                    @onpointerdown:stopPropagation="true" @onmousedown:stopPropagation="true">
                <option value="Tph">Table-per-hierarchy (TPH)</option>
                <option value="Tpt">Table-per-type (TPT)</option>
                <option value="Tpc">Table-per-concrete-type (TPC)</option>
            </select>
        </div>
        @if (_mappingStrategyError is not null)
        {
            <div style="color: red; font-size: 0.8em; padding: 0 8px;">@_mappingStrategyError</div>
        }
    }
    @if (isHierarchyRoot)
    {
        <div style="font-size: 0.8em; margin-top: 4px; padding: 0 8px;">
            <div style="font-weight: bold;">Discriminator:</div>
            <div style="display: flex; gap: 4px; align-items: center; margin: 2px 0;">
                <input style="width: 90px;" value="@Node.Entity.DiscriminatorPropertyName" placeholder="(none)"
                       @onchange="e => CommitDiscriminatorColumn(e.Value?.ToString())"
                       @onpointerdown:stopPropagation="true" @onmousedown:stopPropagation="true" />
                <input style="width: 60px;" value="@Node.Entity.DiscriminatorClrType" placeholder="string"
                       @onchange="e => CommitDiscriminatorClrType(e.Value?.ToString())"
                       @onpointerdown:stopPropagation="true" @onmousedown:stopPropagation="true" />
                @if (Node.Entity.DiscriminatorPropertyName is not null)
                {
                    <button type="button" title="Remove discriminator" aria-label="Remove discriminator" style="border: none; background: transparent; cursor: pointer;"
                            @onclick="RemoveDiscriminator"
                            @onpointerdown:stopPropagation="true" @onmousedown:stopPropagation="true">×</button>
                }
            </div>
            @if (Node.Entity.DiscriminatorPropertyName is not null)
            {
                @foreach (var derived in EditContext.Editor.Current.Entities.Where(e => e.BaseEntityName == Node.Entity.Name))
                {
                    <div style="display: flex; gap: 4px; align-items: center; margin: 2px 0;">
                        <span style="width: 90px; overflow: hidden;">@derived.Name</span>
                        <input style="width: 80px;" value="@derived.DiscriminatorValue" placeholder="(none)"
                               @onchange="e => CommitDiscriminatorValue(derived.Name, e.Value?.ToString())"
                               @onpointerdown:stopPropagation="true" @onmousedown:stopPropagation="true" />
                    </div>
                }
            }
            @if (_discriminatorError is not null)
            {
                <div style="color: red; font-size: 0.8em;">@_discriminatorError</div>
            }
        </div>
    }
```

Add to the `@code` block, near `CommitKeyName`:

```csharp
    private string? _mappingStrategyError;
    private string? _discriminatorError;

    private async Task CommitMappingStrategy(string? newStrategy)
    {
        if (newStrategy is null || !Enum.TryParse<MappingStrategy>(newStrategy, out var strategy))
        {
            return;
        }

        var result = SafeEdit(() => EditContext.Editor.SetMappingStrategy(Node.Entity.Name, strategy));
        if (result.Success)
        {
            _mappingStrategyError = null;
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _mappingStrategyError = result.Error;
        }
    }

    private async Task CommitDiscriminatorColumn(string? columnName)
    {
        var result = SafeEdit(() => EditContext.Editor.SetDiscriminatorColumn(Node.Entity.Name, columnName ?? string.Empty, Node.Entity.DiscriminatorClrType));
        if (result.Success)
        {
            _discriminatorError = null;
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _discriminatorError = result.Error;
        }
    }

    private async Task CommitDiscriminatorClrType(string? clrType)
    {
        if (Node.Entity.DiscriminatorPropertyName is not { } columnName)
        {
            return;
        }

        var result = SafeEdit(() => EditContext.Editor.SetDiscriminatorColumn(Node.Entity.Name, columnName, clrType));
        if (result.Success)
        {
            _discriminatorError = null;
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _discriminatorError = result.Error;
        }
    }

    private async Task CommitDiscriminatorValue(string derivedEntityName, string? value)
    {
        var result = SafeEdit(() => EditContext.Editor.SetDiscriminatorValue(derivedEntityName, value));
        if (result.Success)
        {
            _discriminatorError = null;
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _discriminatorError = result.Error;
        }
    }

    private async Task RemoveDiscriminator()
    {
        var result = SafeEdit(() => EditContext.Editor.RemoveDiscriminatorColumn(Node.Entity.Name));
        if (result.Success)
        {
            _discriminatorError = null;
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _discriminatorError = result.Error;
        }
    }
```

`SafeEdit` is the existing private static helper already declared elsewhere in `EntityNode.razor`'s `@code` block — do not redeclare it.

- [ ] **Step 5: Run the markup-source `SafeEdit` coverage test**

Run: `grep -rl "GestureHandlerSafeEditTests\|SafeEditCoverage" tests/` to find the exact test class name, then:

`dotnet test --filter "FullyQualifiedName~<that class name>"`

Expected: PASS — every new `EditContext.Editor.*` call above follows the existing `SafeEdit(() => ...)` wrapping pattern the test scans for.

- [ ] **Step 6: Run full suite to confirm no regressions**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 7: Manual smoke check**

Use the project's `run` skill (or `dotnet run` in `src/EfSchemaVisualizer.Web`) to launch the Blazor app. Paste a TPH sample with `HasDiscriminator`/`HasValue`, confirm the discriminator panel appears on the base card and edits round-trip; paste a hierarchy, switch its mapping-strategy dropdown to TPT and confirm derived cards lose their non-key inherited properties while the inheritance edge stays; switch to TPC and confirm the edge disappears.

- [ ] **Step 8: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramSync.cs src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor
git commit -m "Render mapping-strategy dropdown, discriminator panel, and suppress TPC inheritance edge"
```

---

### Task 9: Final full-suite verification

**Files:** none (verification only).

- [ ] **Step 1: Run the entire test suite**

Run: `dotnet test`
Expected: PASS, 0 failures.

- [ ] **Step 2: Confirm no stray diagnostics regressed on existing fixtures**

Run: `dotnet test --filter "FullyQualifiedName~DiagramModelBuilderValidityTests"`
Expected: PASS — the 15 existing model-validity tests (from W5) are unaffected, since `InconsistentMappingStrategyInHierarchy` is additive and only fires when explicit strategy calls actually conflict.

- [ ] **Step 3: Re-read the design spec self-review checklist**

Confirm every design-doc section (`docs/superpowers/specs/2026-07-29-inheritance-mapping-strategy-design.md`) has a corresponding implemented task above: Model changes → Task 1/3; Folding → Task 3; Parsing → Task 2; Editing → Task 5/6/7; Rendering → Task 8. No gaps.

- [ ] **Step 4: Update backlog.md**

In `docs/backlog.md`, mark the `Inheritance: HasDiscriminator/HasValue, TPT..., TPC` item done (`- [x]`) with a short "Fixed" note in the same style as the other Priority 2 entries above it (see `SQL-shaped mapping` / `Owned & complex types` entries for the exact format: what changed, what's still out of scope).
