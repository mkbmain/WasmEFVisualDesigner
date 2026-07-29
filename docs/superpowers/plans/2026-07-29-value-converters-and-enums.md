# Value Converters & Enum Storage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Parse, model, and edit EF Core's `HasConversion` fluent calls (type-only and lambda-pair overloads), and always annotate enum-typed properties with how they're actually stored (explicit conversion, or EF's int-default convention).

**Architecture:** Follows the codebase's existing per-feature layering exactly: a `Parse*` method in `FluentConfigParser` reads syntax into a config record, `ModelMerger.Apply*` folds it into `PropertyModel`, `OnModelCreatingRewriter` gets `Set*`/`Remove*` methods (plus owned-property variants built from existing generic helpers), `DiagramEditor` exposes a validated edit method, and `EntityNode.razor` renders/edits it. A new, independent `EntityClassParser.ParseEnumUnderlyingTypes` method and `EnumStorageInference.Fold` module (mirroring `ConventionInference.InferKey`) handle the enum-default annotation, wired into `DiagramModelBuilder.Build` after inheritance folding.

**Tech Stack:** C# / .NET, Roslyn (`Microsoft.CodeAnalysis.CSharp`) for parsing and rewriting, Blazor (`.razor`) for the diagram UI, xUnit for tests.

## Global Constraints

- Every new diagnostic follows the existing `Diagnostic(code, message, entityName, propertyName, span)` shape and is added to `DiagnosticCodes.cs`.
- `HasConversion` is added to `FluentConfigParser.RecognizedCallNames` (a flat `HashSet<string>`) — no `ContextSensitiveCallNames` entry needed since `HasConversion` doesn't collide with any other recognized construct.
- Two `HasConversion` shapes are modeled: type-only (`HasConversion<T>()` / `HasConversion(typeof(T))`) — fully editable — and lambda-pair (`HasConversion(toProvider, fromProvider))` — read-only, display-only. Anything else emits `UnreadableHasConversionArgument` and is not modeled.
- This applies to any property type, not just enums. Enum properties additionally always get `IsEnumType`/`EnumUnderlyingClrType` set, independent of whether an explicit conversion exists.
- No rewriter/editor method is added for the lambda-pair form — only display.
- Follow the file's existing patterns exactly (see each task's "Files" section for the precise template method/line to mirror). Do not introduce new abstractions beyond what's listed.

---

### Task 1: `PropertyModel` fields + `ValueConversionConfig` record

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`
- Create: `src/EfSchemaVisualizer.Core/Merging/ValueConversionConfig.cs`
- Test: none (pure data shape; covered by later tasks' tests)

**Interfaces:**
- Produces: `PropertyModel.ConversionProviderClrType` (`string?`), `PropertyModel.ConversionIsCustomLambda` (`bool?`), `PropertyModel.IsEnumType` (`bool`, default `false`), `PropertyModel.EnumUnderlyingClrType` (`string?`). `ValueConversionConfig(string EntityName, string PropertyName, string? ProviderClrType, bool IsCustomLambda)`.

- [ ] **Step 1: Add the four new fields to `PropertyModel`**

In `src/EfSchemaVisualizer.Core/Model/PropertyModel.cs`, the record currently ends:

```csharp
    string? SequenceName = null,
    string? SequenceSchema = null);
```

Change it to:

```csharp
    string? SequenceName = null,
    string? SequenceSchema = null,
    string? ConversionProviderClrType = null,
    bool? ConversionIsCustomLambda = null,
    bool IsEnumType = false,
    string? EnumUnderlyingClrType = null);
```

- [ ] **Step 2: Create the config record**

Create `src/EfSchemaVisualizer.Core/Merging/ValueConversionConfig.cs`:

```csharp
namespace EfSchemaVisualizer.Core.Merging;

public sealed record ValueConversionConfig(string EntityName, string PropertyName, string? ProviderClrType, bool IsCustomLambda);
```

- [ ] **Step 3: Build to confirm no compile errors**

Run: `dotnet build src/EfSchemaVisualizer.Core/EfSchemaVisualizer.Core.csproj`
Expected: Build succeeds (0 errors). The new fields are additive/optional so no existing call site breaks.

- [ ] **Step 4: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Model/PropertyModel.cs src/EfSchemaVisualizer.Core/Merging/ValueConversionConfig.cs
git commit -m "Add PropertyModel fields and config record for value conversions"
```

---

### Task 2: Parse `HasConversion` in `FluentConfigParser`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`
- Modify: `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs`

**Interfaces:**
- Consumes: `PropertyModel`/`ValueConversionConfig` from Task 1; `FluentSyntaxHelpers.FindConfigurationScopes`, `FluentSyntaxHelpers.FindCallsNamed`, `FluentSyntaxHelpers.GetPropertyNameFor` (all existing, used exactly like `ParseComputedColumnSqls` at `FluentConfigParser.cs:1437-1489`).
- Produces: `FluentConfigParser.ParseValueConversions(string sourceCode) : ParseResult<IReadOnlyList<ValueConversionConfig>>`.

- [ ] **Step 1: Add the new diagnostic code**

In `src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs`, add a line next to `UnreadableHasComputedColumnSqlArgument` (line 21):

```csharp
    public const string UnreadableHasConversionArgument = nameof(UnreadableHasConversionArgument);
```

- [ ] **Step 2: Add `HasConversion` to `RecognizedCallNames`**

In `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, in the `RecognizedCallNames` `HashSet<string>` (lines 27-38), add `"HasConversion"` to the last line's set (next to `"UseTptMappingStrategy", "UseTpcMappingStrategy", "HasDiscriminator",`):

```csharp
        "UseTptMappingStrategy", "UseTpcMappingStrategy", "HasDiscriminator", "HasConversion",
    };
```

- [ ] **Step 3: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs` (near the other `Parse*` test groups, e.g. after the `ParseComputedColumnSqls` tests — search for `// ─── ParseComputedColumnSqls` to find that section and add a new `// ─── ParseValueConversions` section after it):

```csharp
    // ─── ParseValueConversions ──────────────────────────────────────────────────

    [Fact]
    public void ParseValueConversions_GenericTypeArgument_ReadsProviderType()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.Property(e => e.Status).HasConversion<string>();
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseValueConversions(source);

        Assert.Empty(result.Diagnostics);
        var config = Assert.Single(result.Value);
        Assert.Equal("Person", config.EntityName);
        Assert.Equal("Status", config.PropertyName);
        Assert.Equal("string", config.ProviderClrType);
        Assert.False(config.IsCustomLambda);
    }

    [Fact]
    public void ParseValueConversions_TypeOfArgument_ReadsProviderType()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.Property(e => e.Status).HasConversion(typeof(string));
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseValueConversions(source);

        Assert.Empty(result.Diagnostics);
        var config = Assert.Single(result.Value);
        Assert.Equal("string", config.ProviderClrType);
        Assert.False(config.IsCustomLambda);
    }

    [Fact]
    public void ParseValueConversions_LambdaPair_MarksCustomLambda()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.Property(e => e.Status).HasConversion(v => v.ToString(), v => (Status)Enum.Parse(typeof(Status), v));
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseValueConversions(source);

        Assert.Empty(result.Diagnostics);
        var config = Assert.Single(result.Value);
        Assert.Equal("Person", config.EntityName);
        Assert.Equal("Status", config.PropertyName);
        Assert.Null(config.ProviderClrType);
        Assert.True(config.IsCustomLambda);
    }

    [Fact]
    public void ParseValueConversions_ValueConverterInstanceArgument_FlagsUnreadable()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.Property(e => e.Status).HasConversion(new StatusConverter());
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseValueConversions(source);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnreadableHasConversionArgument, diagnostic.Code);
        Assert.Equal("Person", diagnostic.EntityName);
        Assert.Equal("Status", diagnostic.PropertyName);
    }

    [Fact]
    public void ParseValueConversions_NotChainedOnPropertyCall_FlagsUnresolvablePropertyName()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.HasConversion(typeof(string));
                    });
                }
            }
            """;

        var result = new FluentConfigParser().ParseValueConversions(source);

        Assert.Empty(result.Value);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(DiagnosticCodes.UnresolvablePropertyName, diagnostic.Code);
        Assert.Contains("HasConversion", diagnostic.Message);
    }
```

- [ ] **Step 4: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ParseValueConversions"`
Expected: FAIL — `ParseValueConversions` does not exist yet (compile error).

- [ ] **Step 5: Implement `ParseValueConversions`**

Add to `src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs`, directly after `ParseComputedColumnSqls` (which ends at line 1489):

```csharp
    public ParseResult<IReadOnlyList<ValueConversionConfig>> ParseValueConversions(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var results = new List<ValueConversionConfig>();
        var diagnostics = new List<Diagnostic>();

        foreach (var (entityName, scope) in FluentSyntaxHelpers.FindConfigurationScopes(root, _entities))
        {
            foreach (var call in FluentSyntaxHelpers.FindCallsNamed(scope, "HasConversion"))
            {
                var propertyName = FluentSyntaxHelpers.GetPropertyNameFor(call);

                if (propertyName is null)
                {
                    diagnostics.Add(new Diagnostic(
                        DiagnosticCodes.UnresolvablePropertyName,
                        "Could not determine which property this HasConversion call configures.",
                        entityName,
                        PropertyName: null,
                        call.Span));
                    continue;
                }

                if (call.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax { TypeArgumentList.Arguments: [var typeArgNode] } })
                {
                    results.Add(new ValueConversionConfig(entityName, propertyName, typeArgNode.ToString(), IsCustomLambda: false));
                    continue;
                }

                var arguments = call.ArgumentList.Arguments;

                if (arguments.Count == 1 && arguments[0].Expression is TypeOfExpressionSyntax typeOfExpr)
                {
                    results.Add(new ValueConversionConfig(entityName, propertyName, typeOfExpr.Type.ToString(), IsCustomLambda: false));
                    continue;
                }

                if (arguments.Count == 2
                    && arguments[0].Expression is LambdaExpressionSyntax
                    && arguments[1].Expression is LambdaExpressionSyntax)
                {
                    results.Add(new ValueConversionConfig(entityName, propertyName, ProviderClrType: null, IsCustomLambda: true));
                    continue;
                }

                diagnostics.Add(new Diagnostic(
                    DiagnosticCodes.UnreadableHasConversionArgument,
                    "HasConversion argument is not a recognized shape (expected a generic type argument, typeof(...), or two lambda expressions) and could not be read.",
                    entityName,
                    propertyName,
                    (arguments.FirstOrDefault() ?? (SyntaxNode)call).Span));
            }
        }

        return new ParseResult<IReadOnlyList<ValueConversionConfig>>(results, diagnostics);
    }
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ParseValueConversions"`
Expected: PASS (5 tests).

- [ ] **Step 7: Update the existing tests whose behavior changes now that `HasConversion` is recognized**

These 8 existing tests in `FluentConfigParserTests.cs` used `HasConversion` as an example of "some unrecognized call" — several used a shape not chained onto a `Property(...)` call, so after Step 2 they now surface `UnresolvablePropertyName` instead of `UnrecognizedConfigCall` (the message still contains "HasConversion" since `UnresolvablePropertyName`'s message includes the method name, so most `Assert.Contains` checks still pass unchanged). Handle them as follows:

1. **`ParseUnrecognizedCalls_FlagsCallNotReadByAnyParser`** (around line 2897-2919) — this test's actual purpose is "some unrecognized call is flagged", using `HasConversion` as the example. Since `HasConversion` is no longer unrecognized, retarget the example to a genuinely still-unrecognized call. Change the source's `entity.HasConversion(typeof(string));` to `entity.HasAnnotation("Foo", "Bar");` and the assertion `Assert.Contains("HasConversion", diagnostic.Message);` to `Assert.Contains("HasAnnotation", diagnostic.Message);`. Leave `Assert.Equal(DiagnosticCodes.UnrecognizedConfigCall, diagnostic.Code);` unchanged.

2. **`ParseUnrecognizedCalls_ChainedAfterRecognizedCall_IsFlagged`** (around line 2921-2941) — source is `entity.HasIndex(e => e.Email).IsUnique().HasConversion(typeof(string));`, chained onto `HasIndex(...)`, not `Property(...)`, so `GetPropertyNameFor` still returns null. No change needed — the diagnostic becomes `UnresolvablePropertyName` but the test doesn't assert `.Code`, only `Assert.Contains("HasConversion", diagnostic.Message)`, which still passes. Leave as-is, but add a one-line comment above it: `// HasConversion here is unresolvable (not chained on Property(...)), so this now asserts UnresolvablePropertyName rather than UnrecognizedConfigCall — message still contains "HasConversion".`

3. **`ParseUnrecognizedCalls_NestedEntityConfig_DoesNotAttributeToOuterEntity`** (around line 3016-3041) — only asserts `diagnostic.EntityName`, unaffected. No change needed.

4. **`ParseUnrecognizedCalls_BareChainedStyle_FlagsUnrecognizedTailCall`** (around line 3043-3060) — source `modelBuilder.Entity<Person>().HasConversion(e => e.ToString());`, only asserts `Assert.Contains("HasConversion", diagnostic.Message)`. No change needed (still passes via `UnresolvablePropertyName`'s message).

5. **`ParseUnrecognizedCalls_EntityTypeConfigurationStyle_FlagsUnrecognizedCall`** (around line 3080-3098) — same pattern, only asserts `EntityName` and `Contains(message)`. No change needed.

6. **`ParseUnrecognizedModelLevelCalls_ChainedOntoEntityResult_IsNotFlaggedHere`** (around line 4054-4073) — asserts `Assert.Empty(diagnostics)` from `ParseUnrecognizedModelLevelCalls`, a different method entirely that only looks at model-level (`modelBuilder.`-receiver) calls, not entity-scoped ones. Unaffected. No change needed.

- [ ] **Step 8: Run the full parser test file to confirm nothing else broke**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~FluentConfigParserTests"`
Expected: PASS, all tests green.

- [ ] **Step 9: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/FluentConfigParser.cs src/EfSchemaVisualizer.Core/Parsing/DiagnosticCodes.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/FluentConfigParserTests.cs
git commit -m "Parse HasConversion (type-only and lambda-pair overloads)"
```

---

### Task 3: `ModelMerger.ApplyValueConversions` + wire into `DiagramModelBuilder`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs`

**Interfaces:**
- Consumes: `ValueConversionConfig` (Task 1), `FluentConfigParser.ParseValueConversions` (Task 2).
- Produces: `ModelMerger.ApplyValueConversions(EntityModel entity, IReadOnlyList<ValueConversionConfig> configs) : EntityModel`.

- [ ] **Step 1: Write the failing merger test**

Add to `tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs`, after the `ApplyComputedColumnSqls` test (search `// ─── ApplyComputedColumnSqls`, add a new section right after its closing `}`):

```csharp
    // ─── ApplyValueConversions ──────────────────────────────────────────────────

    [Fact]
    public void ApplyValueConversions_SetsProviderTypeAndLambdaFlagOnMatchingProperty_LeavesOthersUntouched()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Status", "Status", IsNullable: false, MaxLength: null),
            new("Name", "string", IsNullable: false, MaxLength: null),
        });

        var configs = new List<ValueConversionConfig>
        {
            new("Person", "Status", "string", IsCustomLambda: false),
        };

        var result = ModelMerger.ApplyValueConversions(entity, configs);

        var status = result.Properties.Single(p => p.Name == "Status");
        Assert.Equal("string", status.ConversionProviderClrType);
        Assert.False(status.ConversionIsCustomLambda);

        var name = result.Properties.Single(p => p.Name == "Name");
        Assert.Null(name.ConversionProviderClrType);
        Assert.Null(name.ConversionIsCustomLambda);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ApplyValueConversions"`
Expected: FAIL — `ApplyValueConversions` does not exist.

- [ ] **Step 3: Implement `ApplyValueConversions`**

Add to `src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs`, directly after `ApplyComputedColumnSqls` (currently ends at line 267):

```csharp
    public static EntityModel ApplyValueConversions(EntityModel entity, IReadOnlyList<ValueConversionConfig> configs)
    {
        var byProperty = IndexByProperty(entity.Name, configs, c => c.EntityName, c => c.PropertyName);

        var updatedProperties = entity.Properties
            .Select(property => byProperty.TryGetValue(property.Name, out var config)
                ? property with { ConversionProviderClrType = config.ProviderClrType, ConversionIsCustomLambda = config.IsCustomLambda }
                : property)
            .ToList();

        return entity with { Properties = updatedProperties };
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ApplyValueConversions"`
Expected: PASS.

- [ ] **Step 5: Wire into `DiagramModelBuilder.Build`**

In `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`:

Add, next to line 46 (`var computedColumnSqls = configParser.ParseComputedColumnSqls(configSource);`):

```csharp
        var valueConversions = configParser.ParseValueConversions(configSource);
```

Add, next to line 87 (`diagnostics.AddRange(computedColumnSqls.Diagnostics);`):

```csharp
        diagnostics.AddRange(valueConversions.Diagnostics);
```

Add, in the entity `.Select(...)` chain, directly after line 139 (`.Select(entity => ModelMerger.ApplyComputedColumnSqls(entity, computedColumnSqls.Value))`):

```csharp
            .Select(entity => ModelMerger.ApplyValueConversions(entity, valueConversions.Value))
```

- [ ] **Step 6: Write the failing `DiagramModelBuilder`-level test**

Add to `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs`:

```csharp
    [Fact]
    public void Build_HasConversionOnStatusProperty_SetsConversionProviderClrType()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }
                public Status Status { get; set; }
            }

            public enum Status
            {
                Active,
                Inactive,
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.Property(e => e.Status).HasConversion<string>();
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var status = result.Entities.Single().Properties.Single(p => p.Name == "Status");
        Assert.Equal("string", status.ConversionProviderClrType);
    }
```

- [ ] **Step 7: Run test to verify it fails, then passes**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "FullyQualifiedName~Build_HasConversionOnStatusProperty"`
Expected: FAIL before Step 5's wiring is in place is not applicable here (Step 5 already done) — this test should already PASS once run, since the merge wiring is already added. If it fails, check that Step 5's three edits were applied correctly.

- [ ] **Step 8: Run the full Core and Web test suites**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests && dotnet test tests/EfSchemaVisualizer.Web.Tests`
Expected: PASS, all green.

- [ ] **Step 9: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Merging/ModelMerger.cs src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs tests/EfSchemaVisualizer.Core.Tests/Merging/ModelMergerTests.cs tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs
git commit -m "Merge parsed value conversions into PropertyModel"
```

---

### Task 4: Enum detection (`EntityClassParser.ParseEnumUnderlyingTypes`) + `EnumStorageInference`

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs`
- Create: `src/EfSchemaVisualizer.Core/Inference/EnumStorageInference.cs`
- Modify: `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/Inference/EnumStorageInferenceTests.cs` (new file)
- Test: `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs`

**Interfaces:**
- Produces: `EntityClassParser.ParseEnumUnderlyingTypes(string sourceCode) : IReadOnlyDictionary<string, string>` (enum name → underlying type, `"int"` default). `EnumStorageInference.Fold(IReadOnlyList<EntityModel> entities, IReadOnlyDictionary<string, string> enumUnderlyingTypes) : IReadOnlyList<EntityModel>`.

- [ ] **Step 1: Write the failing `EntityClassParser` test**

Add to `tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs` (add near the end of the file, or after any existing attribute-parsing test group):

```csharp
    [Fact]
    public void ParseEnumUnderlyingTypes_PlainEnum_DefaultsToInt()
    {
        const string source = """
            public enum Status
            {
                Active,
                Inactive,
            }
            """;

        var result = new EntityClassParser().ParseEnumUnderlyingTypes(source);

        Assert.Equal("int", result["Status"]);
    }

    [Fact]
    public void ParseEnumUnderlyingTypes_ExplicitBaseType_ReadsUnderlyingType()
    {
        const string source = """
            public enum Status : byte
            {
                Active,
                Inactive,
            }
            """;

        var result = new EntityClassParser().ParseEnumUnderlyingTypes(source);

        Assert.Equal("byte", result["Status"]);
    }

    [Fact]
    public void ParseEnumUnderlyingTypes_NoEnums_ReturnsEmpty()
    {
        const string source = """
            public class Person
            {
                public int Id { get; set; }
            }
            """;

        var result = new EntityClassParser().ParseEnumUnderlyingTypes(source);

        Assert.Empty(result);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ParseEnumUnderlyingTypes"`
Expected: FAIL — method does not exist.

- [ ] **Step 3: Implement `ParseEnumUnderlyingTypes`**

Add to `src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs`, directly after `ParseRelationships` (the method ending around line 444, which is the last public method shown near the bottom of the file — add this as a new public method following the same independent-parse-pass pattern used by `ParseRelationships`/`ParseIndexAttributes`):

```csharp
    public IReadOnlyDictionary<string, string> ParseEnumUnderlyingTypes(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var result = new Dictionary<string, string>();

        foreach (var enumDeclaration in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
        {
            var underlyingType = enumDeclaration.BaseList?.Types.FirstOrDefault()?.Type.ToString() ?? "int";
            result[enumDeclaration.Identifier.Text] = underlyingType;
        }

        return result;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ParseEnumUnderlyingTypes"`
Expected: PASS (3 tests).

- [ ] **Step 5: Write the failing `EnumStorageInference` test**

Create `tests/EfSchemaVisualizer.Core.Tests/Inference/EnumStorageInferenceTests.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Inference;
using EfSchemaVisualizer.Core.Model;
using Xunit;

namespace EfSchemaVisualizer.Core.Tests.Inference;

public class EnumStorageInferenceTests
{
    [Fact]
    public void Fold_PropertyClrTypeMatchesKnownEnum_SetsIsEnumTypeAndUnderlyingType()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Status", "Status", IsNullable: false, MaxLength: null),
            new("Name", "string", IsNullable: false, MaxLength: null),
        });

        var enumUnderlyingTypes = new Dictionary<string, string> { ["Status"] = "int" };

        var result = EnumStorageInference.Fold(new[] { entity }, enumUnderlyingTypes);

        var status = result.Single().Properties.Single(p => p.Name == "Status");
        Assert.True(status.IsEnumType);
        Assert.Equal("int", status.EnumUnderlyingClrType);

        var name = result.Single().Properties.Single(p => p.Name == "Name");
        Assert.False(name.IsEnumType);
        Assert.Null(name.EnumUnderlyingClrType);
    }

    [Fact]
    public void Fold_NoMatchingEnum_LeavesPropertyUnchanged()
    {
        var entity = new EntityModel("Person", new List<PropertyModel>
        {
            new("Status", "UnknownType", IsNullable: false, MaxLength: null),
        });

        var result = EnumStorageInference.Fold(new[] { entity }, new Dictionary<string, string>());

        var status = result.Single().Properties.Single(p => p.Name == "Status");
        Assert.False(status.IsEnumType);
    }
}
```

- [ ] **Step 6: Run test to verify it fails**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~EnumStorageInferenceTests"`
Expected: FAIL — `EnumStorageInference` does not exist.

- [ ] **Step 7: Implement `EnumStorageInference`**

Create `src/EfSchemaVisualizer.Core/Inference/EnumStorageInference.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using EfSchemaVisualizer.Core.Model;

namespace EfSchemaVisualizer.Core.Inference;

public static class EnumStorageInference
{
    public static IReadOnlyList<EntityModel> Fold(IReadOnlyList<EntityModel> entities, IReadOnlyDictionary<string, string> enumUnderlyingTypes)
    {
        return entities.Select(entity => Fold(entity, enumUnderlyingTypes)).ToList();
    }

    private static EntityModel Fold(EntityModel entity, IReadOnlyDictionary<string, string> enumUnderlyingTypes)
    {
        var updatedProperties = entity.Properties
            .Select(property => enumUnderlyingTypes.TryGetValue(property.ClrType, out var underlyingType)
                ? property with { IsEnumType = true, EnumUnderlyingClrType = underlyingType }
                : property)
            .ToList();

        return entity with { Properties = updatedProperties };
    }
}
```

- [ ] **Step 8: Run test to verify it passes**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~EnumStorageInferenceTests"`
Expected: PASS (2 tests).

- [ ] **Step 9: Wire into `DiagramModelBuilder.Build`**

In `src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs`:

Add, right after line 22 (`var entityResult = entityParser.Parse(classSource);`):

```csharp
        var enumUnderlyingTypes = entityParser.ParseEnumUnderlyingTypes(classSource);
```

Add `using EfSchemaVisualizer.Core.Inference;` is already present (line 2) — no new using needed since `EnumStorageInference` lives in that same namespace.

Add, right after the existing line `entities = inheritanceFold.Entities;` (currently line 161):

```csharp
        entities = EnumStorageInference.Fold(entities, enumUnderlyingTypes);
```

- [ ] **Step 10: Write the failing `DiagramModelBuilder`-level test for the enum-default case**

Add to `tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs`:

```csharp
    [Fact]
    public void Build_EnumPropertyWithNoHasConversion_AnnotatesDefaultIntStorage()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }
                public Status Status { get; set; }
            }

            public enum Status : byte
            {
                Active,
                Inactive,
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var status = result.Entities.Single().Properties.Single(p => p.Name == "Status");
        Assert.True(status.IsEnumType);
        Assert.Equal("byte", status.EnumUnderlyingClrType);
        Assert.Null(status.ConversionProviderClrType);
    }

    [Fact]
    public void Build_EnumPropertyWithExplicitHasConversion_ShowsExplicitConversionAlongsideEnumFlag()
    {
        const string classSource = """
            public class Person
            {
                public int Id { get; set; }
                public Status Status { get; set; }
            }

            public enum Status
            {
                Active,
                Inactive,
            }
            """;

        const string configSource = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Person>(entity =>
                    {
                        entity.Property(e => e.Status).HasConversion<string>();
                    });
                }
            }
            """;

        var result = DiagramModelBuilder.Build(classSource, configSource);

        var status = result.Entities.Single().Properties.Single(p => p.Name == "Status");
        Assert.True(status.IsEnumType);
        Assert.Equal("string", status.ConversionProviderClrType);
    }
```

- [ ] **Step 11: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "FullyQualifiedName~Build_EnumProperty"`
Expected: PASS (2 tests).

- [ ] **Step 12: Run the full Core and Web test suites**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests && dotnet test tests/EfSchemaVisualizer.Web.Tests`
Expected: PASS, all green.

- [ ] **Step 13: Commit**

```bash
git add src/EfSchemaVisualizer.Core/Parsing/EntityClassParser.cs src/EfSchemaVisualizer.Core/Inference/EnumStorageInference.cs src/EfSchemaVisualizer.Web/DiagramModelBuilder.cs tests/EfSchemaVisualizer.Core.Tests/Parsing/EntityClassParserTests.cs tests/EfSchemaVisualizer.Core.Tests/Inference/EnumStorageInferenceTests.cs tests/EfSchemaVisualizer.Web.Tests/DiagramModelBuilderTests.cs
git commit -m "Detect enum declarations and annotate default int storage"
```

---

### Task 5: `OnModelCreatingRewriter` — `SetValueConversion`/`RemoveValueConversion` (+ owned variants)

**Files:**
- Modify: `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`
- Test: `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterOwnedConfigScopeTests.cs`

**Interfaces:**
- Consumes: `FluentSyntaxHelpers.FindCallsNamed`, `FluentSyntaxHelpers.GetPropertyNameFor`, `FluentSyntaxHelpers.GetPropertyNameForPropertyCall`, `FluentSyntaxHelpers.GetPropertyLambdaParameterName` (all existing). `FindConfigScopes`, `GetScopeBlockAndReceiver`, `FindOnModelCreatingMethod`, `BuildEntityInvocationStatement`, `RemoveStringArgCall`, `SetOnOwnedProperty`, `RemoveOnOwnedProperty` (all existing private/internal helpers already used by `SetComputedColumnSql`/`SetColumnNameOnOwnedProperty` etc.).
- Produces: `OnModelCreatingRewriter.SetValueConversion(string sourceCode, string entityName, string propertyName, string providerClrType) : string`, `RemoveValueConversion(string sourceCode, string entityName, string propertyName) : string`, `SetValueConversionOnOwnedProperty(string sourceCode, string ownerEntityName, string navPropertyName, string propertyName, string providerClrType) : string`, `RemoveValueConversionOnOwnedProperty(string sourceCode, string ownerEntityName, string navPropertyName, string propertyName) : string`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs`, directly after the `RemoveComputedColumnSql_ExistingCall_RemovesCall_LeavesBarePropertyCall` test (ends around line 2410), reusing the existing `SourceWithPropertyButNoDefaultValue` fixture (an `Order`/`Quantity` property with a bare `Property(e => e.Quantity)` call, defined at line 2270):

```csharp
    [Fact]
    public void SetValueConversion_BarePropertyCall_AppendsGenericHasConversion()
    {
        var result = new OnModelCreatingRewriter()
            .SetValueConversion(SourceWithPropertyButNoDefaultValue, entityName: "Order", propertyName: "Quantity", providerClrType: "string");

        Assert.Contains("entity.Property(e => e.Quantity).HasConversion<string>()", result);
    }

    [Fact]
    public void SetValueConversion_ExistingCall_MutatesToNewProviderType()
    {
        var source = new OnModelCreatingRewriter()
            .SetValueConversion(SourceWithPropertyButNoDefaultValue, entityName: "Order", propertyName: "Quantity", providerClrType: "string");

        var result = new OnModelCreatingRewriter()
            .SetValueConversion(source, entityName: "Order", propertyName: "Quantity", providerClrType: "int");

        Assert.Contains("entity.Property(e => e.Quantity).HasConversion<int>()", result);
        Assert.DoesNotContain("HasConversion<string>", result);
    }

    [Fact]
    public void RemoveValueConversion_ExistingCall_RemovesCall_LeavesBarePropertyCall()
    {
        var source = new OnModelCreatingRewriter()
            .SetValueConversion(SourceWithPropertyButNoDefaultValue, entityName: "Order", propertyName: "Quantity", providerClrType: "string");

        var result = new OnModelCreatingRewriter()
            .RemoveValueConversion(source, entityName: "Order", propertyName: "Quantity");

        Assert.DoesNotContain("HasConversion", result);
        Assert.Contains("entity.Property(e => e.Quantity)", result);
    }

    [Fact]
    public void SetValueConversion_NoExistingPropertyCall_InsertsNewStatement()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.HasKey(e => e.Id);
                    });
                }
            }
            """;

        var result = new OnModelCreatingRewriter()
            .SetValueConversion(source, entityName: "Order", propertyName: "Quantity", providerClrType: "string");

        Assert.Contains("entity.Property(e => e.Quantity).HasConversion<string>()", result);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ValueConversion"`
Expected: FAIL — methods do not exist.

- [ ] **Step 3: Implement the type-arg builder helpers**

Add to `src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs`, directly after `BuildStringArgCall` (currently ends at line 1115, just before `RemoveStringArgCall`):

```csharp
    private static InvocationExpressionSyntax BuildTypeArgCall(ExpressionSyntax receiverExpression, string methodName, string typeArgText)
    {
        SimpleNameSyntax name = SyntaxFactory.GenericName(SyntaxFactory.Identifier(methodName))
            .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList<TypeSyntax>(SyntaxFactory.ParseTypeName(typeArgText))));

        return SyntaxFactory.InvocationExpression(
            SyntaxFactory.MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, receiverExpression, name),
            SyntaxFactory.ArgumentList());
    }

    private static string MutateExistingTypeArgCall(CompilationUnitSyntax root, InvocationExpressionSyntax targetCall, string typeArgText)
    {
        var receiverExpression = ((MemberAccessExpressionSyntax)targetCall.Expression).Expression;
        var newCall = BuildTypeArgCall(receiverExpression, "HasConversion", typeArgText);

        var newRoot = root.ReplaceNode(targetCall, newCall);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string AppendTypeArgCallToPropertyCall(CompilationUnitSyntax root, InvocationExpressionSyntax propertyCall, string typeArgText)
    {
        var newCall = BuildTypeArgCall(propertyCall, "HasConversion", typeArgText);

        var newRoot = root.ReplaceNode(propertyCall, newCall);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertTypeArgPropertyStatement(CompilationUnitSyntax root, SyntaxNode scope, string propertyName, string typeArgText)
    {
        var (block, blockReceiverName) = GetScopeBlockAndReceiver(scope);
        var propertyLambdaParam = FluentSyntaxHelpers.GetPropertyLambdaParameterName(scope);

        var newStatement = BuildTypeArgPropertyStatement(blockReceiverName, propertyLambdaParam, propertyName, typeArgText);
        var newBlock = block.AddStatements(newStatement);

        var newRoot = root.ReplaceNode(block, newBlock);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static string InsertTypeArgEntityBlock(CompilationUnitSyntax root, string entityName, string propertyName, string typeArgText)
    {
        var method = FindOnModelCreatingMethod(root);

        var methodBody = method.Body
            ?? throw new InvalidOperationException("OnModelCreating has no method body.");

        var modelBuilderParamName = method.ParameterList.Parameters.Single().Identifier.Text;

        var propertyStatement = BuildTypeArgPropertyStatement("entity", "e", propertyName, typeArgText);
        var entityBlockStatement = BuildEntityInvocationStatement(modelBuilderParamName, entityName, SyntaxFactory.Block(propertyStatement));

        var newMethodBody = methodBody.AddStatements(entityBlockStatement);
        var newRoot = root.ReplaceNode(methodBody, newMethodBody);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    private static ExpressionStatementSyntax BuildTypeArgPropertyStatement(string blockReceiverName, string propertyLambdaParam, string propertyName, string typeArgText)
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

        return SyntaxFactory.ExpressionStatement(BuildTypeArgCall(propertyCall, "HasConversion", typeArgText));
    }
```

- [ ] **Step 4: Implement `SetValueConversion`/`RemoveValueConversion`**

Add directly after `RemoveComputedColumnSql` (currently ends at line 1358, right before `SetUseSequence`'s section — search for `public string RemoveComputedColumnSql`):

```csharp
    public string SetValueConversion(string sourceCode, string entityName, string propertyName, string providerClrType)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();

        var scopes = FindConfigScopes(root, entityName);

        var existingCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "HasConversion"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameFor(call) == propertyName);

        if (existingCall is not null)
        {
            return MutateExistingTypeArgCall(root, existingCall, providerClrType);
        }

        var existingPropertyCall = scopes
            .SelectMany(scope => FluentSyntaxHelpers.FindCallsNamed(scope, "Property"))
            .FirstOrDefault(call => FluentSyntaxHelpers.GetPropertyNameForPropertyCall(call) == propertyName);

        if (existingPropertyCall is not null)
        {
            return AppendTypeArgCallToPropertyCall(root, existingPropertyCall, providerClrType);
        }

        var existingScope = scopes.FirstOrDefault();

        if (existingScope is not null)
        {
            return InsertTypeArgPropertyStatement(root, existingScope, propertyName, providerClrType);
        }

        return InsertTypeArgEntityBlock(root, entityName, propertyName, providerClrType);
    }

    public string RemoveValueConversion(string sourceCode, string entityName, string propertyName)
    {
        return RemoveStringArgCall(sourceCode, entityName, propertyName, "HasConversion");
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ValueConversion"`
Expected: PASS (4 tests).

- [ ] **Step 6: Write the failing owned-property tests**

The owned-property rewriter tests live in a separate file, `tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterOwnedConfigScopeTests.cs`, which already has a `SetColumnNameOnOwnedProperty` test using an `Order` entity with a bare `entity.OwnsOne(e => e.ShippingAddress);` call and a `Street` property (see `SetColumnName_OnFoldedOwnedProperty_SynthesizesBuilderLambdaOnBareOwnsOneCall`, lines 10-30). Add two new tests to that same file, after the existing `SetColumnName_On...` tests:

```csharp
    [Fact]
    public void SetValueConversionOnOwnedProperty_BareOwnsOneCall_SynthesizesBuilderLambdaWithGenericHasConversion()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.OwnsOne(e => e.ShippingAddress);
                    });
                }
            }
            """;

        var newSource = _rewriter.SetValueConversionOnOwnedProperty(source, "Order", "ShippingAddress", "Street", "string");

        Assert.Contains("OwnsOne(e => e.ShippingAddress, b =>", newSource);
        Assert.Contains("HasConversion<string>()", newSource);
    }

    [Fact]
    public void RemoveValueConversionOnOwnedProperty_ExistingCall_RemovesCall()
    {
        const string source = """
            public class AppDbContext : DbContext
            {
                protected override void OnModelCreating(ModelBuilder modelBuilder)
                {
                    modelBuilder.Entity<Order>(entity =>
                    {
                        entity.OwnsOne(e => e.ShippingAddress);
                    });
                }
            }
            """;

        var withConversion = _rewriter.SetValueConversionOnOwnedProperty(source, "Order", "ShippingAddress", "Street", "string");

        var result = _rewriter.RemoveValueConversionOnOwnedProperty(withConversion, "Order", "ShippingAddress", "Street");

        Assert.DoesNotContain("HasConversion", result);
    }
```

- [ ] **Step 7: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ValueConversionOnOwnedProperty"`
Expected: FAIL — methods do not exist.

- [ ] **Step 8: Implement the owned-property variants**

Add directly after `RemoveComputedColumnSqlOnOwnedProperty` (currently ends at line 2314, right before the `UseSequence`-on-owned section):

```csharp
    public string SetValueConversionOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName, string providerClrType) =>
        SetOnOwnedProperty(sourceCode, ownerEntityName, navPropertyName, propertyName, "HasConversion",
            expr => BuildTypeArgCall(expr, "HasConversion", providerClrType));

    public string RemoveValueConversionOnOwnedProperty(
        string sourceCode, string ownerEntityName, string navPropertyName, string propertyName) =>
        RemoveOnOwnedProperty(sourceCode, ownerEntityName, navPropertyName, propertyName, "HasConversion");
```

- [ ] **Step 9: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests --filter "FullyQualifiedName~ValueConversionOnOwnedProperty"`
Expected: PASS (2 tests).

- [ ] **Step 10: Run the full Core test suite**

Run: `dotnet test tests/EfSchemaVisualizer.Core.Tests`
Expected: PASS, all green.

- [ ] **Step 11: Commit**

```bash
git add src/EfSchemaVisualizer.Core/CodeGen/OnModelCreatingRewriter.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterTests.cs tests/EfSchemaVisualizer.Core.Tests/CodeGen/OnModelCreatingRewriterOwnedConfigScopeTests.cs
git commit -m "Rewrite HasConversion (set/remove, including owned-property variants)"
```

---

### Task 6: `DiagramEditor.SetValueConversion`

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`
- Test: `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs`

**Interfaces:**
- Consumes: `OnModelCreatingRewriter.SetValueConversion`/`RemoveValueConversion`/`SetValueConversionOnOwnedProperty`/`RemoveValueConversionOnOwnedProperty` (Task 5). Existing `DiagramEditor` helpers: `IsValidTypeToken`, `ResolveDeclaringEntity`, `ValidateOwnedEditDepth`, `Apply`, `DiagramEditResult.Ok()`/`Fail(string)`.
- Produces: `DiagramEditor.SetValueConversion(string entityName, string propertyName, string? providerClrType) : DiagramEditResult`.

- [ ] **Step 1: Write the failing tests**

Add to `tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs`, directly after `SetComputedColumnSql_ClearingExistingConfig_RemovesHasComputedColumnSql` (ends around line 602), reusing the file's `ClassSource`/`ConfigSource` fixtures (a `Person` entity with `Id`/`Name`, defined at the top of the file):

```csharp
    [Fact]
    public void SetValueConversion_NoExistingConfig_InsertsHasConversion()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetValueConversion("Person", "Name", "string");

        Assert.True(result.Success);
        var property = editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name");
        Assert.Equal("string", property.ConversionProviderClrType);
        Assert.Contains("HasConversion<string>()", editor.ConfigSource);
    }

    [Fact]
    public void SetValueConversion_ClearingExistingConfig_RemovesHasConversion()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.SetValueConversion("Person", "Name", "string");

        var result = editor.SetValueConversion("Person", "Name", null);

        Assert.True(result.Success);
        Assert.Null(editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name").ConversionProviderClrType);
        Assert.DoesNotContain("HasConversion", editor.ConfigSource);
    }

    [Fact]
    public void SetValueConversion_InvalidTypeToken_Fails()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);

        var result = editor.SetValueConversion("Person", "Name", "not a type!!");

        Assert.False(result.Success);
        Assert.Null(editor.Current.Entities.Single().Properties.Single(p => p.Name == "Name").ConversionProviderClrType);
    }

    [Fact]
    public void SetValueConversion_UnchangedValue_IsNoOp()
    {
        var editor = new DiagramEditor(ClassSource, ConfigSource);
        editor.SetValueConversion("Person", "Name", "string");
        var configBefore = editor.ConfigSource;

        var result = editor.SetValueConversion("Person", "Name", "string");

        Assert.True(result.Success);
        Assert.Equal(configBefore, editor.ConfigSource);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "FullyQualifiedName~DiagramEditorPropertyPanelTests.SetValueConversion"`
Expected: FAIL — `SetValueConversion` does not exist on `DiagramEditor`.

- [ ] **Step 3: Implement `DiagramEditor.SetValueConversion`**

Add to `src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs`, directly after `SetComputedColumnSql` (currently ends at line 1261, right before the `QuotedDefaultValueClrTypes` field):

```csharp
    public DiagramEditResult SetValueConversion(string entityName, string propertyName, string? providerClrType)
    {
        var entity = Current.Entities.FirstOrDefault(e => e.Name == entityName);
        if (entity is null)
        {
            return DiagramEditResult.Fail($"Entity '{entityName}' not found.");
        }

        var property = entity.Properties.FirstOrDefault(p => p.Name == propertyName);
        if (property is null)
        {
            return DiagramEditResult.Fail($"Property '{propertyName}' not found on '{entityName}'.");
        }

        var normalizedType = string.IsNullOrWhiteSpace(providerClrType) ? null : providerClrType.Trim();

        if (normalizedType is not null && !IsValidTypeToken(normalizedType))
        {
            return DiagramEditResult.Fail($"'{normalizedType}' is not a valid type.");
        }

        if (normalizedType == property.ConversionProviderClrType)
        {
            return DiagramEditResult.Ok();
        }

        string newConfigSource;
        if (property.FoldKind != FoldKind.None && property.OwnerNavigationProperty is { } conversionNav)
        {
            if (ValidateOwnedEditDepth(entityName, property, conversionNav) is { } foldFailure)
            {
                return foldFailure;
            }

            newConfigSource = normalizedType is null
                ? _configRewriter.RemoveValueConversionOnOwnedProperty(ConfigSource, entityName, conversionNav, propertyName)
                : _configRewriter.SetValueConversionOnOwnedProperty(ConfigSource, entityName, conversionNav, propertyName, normalizedType);
        }
        else
        {
            newConfigSource = normalizedType is null
                ? _configRewriter.RemoveValueConversion(ConfigSource, ResolveDeclaringEntity(entityName, propertyName), propertyName)
                : _configRewriter.SetValueConversion(ConfigSource, ResolveDeclaringEntity(entityName, propertyName), propertyName, normalizedType);
        }

        Apply(ClassSource, newConfigSource);
        return DiagramEditResult.Ok();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "FullyQualifiedName~DiagramEditorPropertyPanelTests.SetValueConversion"`
Expected: PASS (4 tests).

- [ ] **Step 5: Run the full Web test suite**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests`
Expected: PASS, all green.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/DiagramEditor.cs tests/EfSchemaVisualizer.Web.Tests/Diagram/DiagramEditorPropertyPanelTests.cs
git commit -m "Add DiagramEditor.SetValueConversion"
```

---

### Task 7: UI — `EntityNode.razor` "Stored as" row + enum-default hint

**Files:**
- Modify: `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`

**Interfaces:**
- Consumes: `PropertyModel.ConversionProviderClrType`/`ConversionIsCustomLambda`/`IsEnumType`/`EnumUnderlyingClrType` (Task 1), `EditContext.Editor.SetValueConversion` (Task 6), existing `SafeEdit` helper, existing `_propertyErrors` dictionary.

This is a UI-only task with no automated test — verify manually per Step 4 (per the project convention that Blazor UI changes are checked by running the app; xUnit doesn't cover `.razor` markup here beyond the existing `GestureHandlerSafeEditTests` markup-source scan, which Step 3 keeps satisfied).

- [ ] **Step 1: Add the editable "Stored as" row to the expanded property panel**

In `src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor`, directly after the "Uses sequence" `<label>` block (lines 382-388):

```razor
                                <label style="display: block;">
                                    Uses sequence:
                                    <input list="sequence-names" style="width: 100px;" value="@property.SequenceName" placeholder="(none)"
                                           @onchange="e => CommitUseSequence(property, e.Value?.ToString())"
                                           @onpointerdown:stopPropagation="true"
                                           @onmousedown:stopPropagation="true" />
                                </label>
                                @if (property.ConversionIsCustomLambda == true)
                                {
                                    <div style="font-size: 0.8em; color: #888; font-style: italic;" title="A custom two-lambda HasConversion is configured in source; this is read-only here.">
                                        Stored as: custom conversion
                                    </div>
                                }
                                else
                                {
                                    <label style="display: block;">
                                        Stored as:
                                        <input list="conversion-provider-types" style="width: 100px;"
                                               value="@property.ConversionProviderClrType"
                                               placeholder="@(property.IsEnumType ? $"({property.EnumUnderlyingClrType} default)" : "(none)")"
                                               @onchange="e => CommitValueConversion(property, e.Value?.ToString())"
                                               @onpointerdown:stopPropagation="true"
                                               @onmousedown:stopPropagation="true" />
                                    </label>
                                }
```

- [ ] **Step 2: Add the provider-type datalist and the collapsed-row muted hint**

Add a new `<datalist>` next to the existing `sequence-names` one (lines 9-14):

```razor
<datalist id="conversion-provider-types">
    <option value="string" />
    <option value="int" />
    <option value="long" />
    <option value="short" />
    <option value="byte" />
</datalist>
```

In the collapsed property row, directly after the `ValueGenerated` badge block (lines 261-265):

```razor
                    @if (property.ConversionProviderClrType is not null)
                    {
                        <span style="font-size: 0.7em; color: #888; margin-left: 4px;" title="Explicit HasConversion in source.">→ @property.ConversionProviderClrType</span>
                    }
                    else if (property.ConversionIsCustomLambda == true)
                    {
                        <span style="font-size: 0.7em; color: #888; margin-left: 4px; font-style: italic;" title="Custom two-lambda HasConversion in source.">→ custom</span>
                    }
                    else if (property.IsEnumType)
                    {
                        <span style="font-size: 0.7em; color: #888; margin-left: 4px; opacity: 0.6;" title="EF's convention default: no explicit HasConversion, so this enum is stored as its underlying type.">→ @property.EnumUnderlyingClrType (default)</span>
                    }
```

- [ ] **Step 3: Add the `CommitValueConversion` handler**

Add to the `@code` block, directly after `CommitComputedColumnSqlIsStored` (currently ends at line 1241):

```csharp
    private async Task CommitValueConversion(PropertyModel property, string? newProviderType)
    {
        var result = SafeEdit(() => EditContext.Editor.SetValueConversion(Node.Entity.Name, property.Name, newProviderType));
        if (result.Success)
        {
            _propertyErrors.Remove(property.Name);
            await EditContext.NotifyChangedAsync();
        }
        else
        {
            _propertyErrors[property.Name] = result.Error!;
        }
    }
```

- [ ] **Step 4: Build and manually verify in the running app**

Run: `dotnet build src/EfSchemaVisualizer.Web/EfSchemaVisualizer.Web.csproj`
Expected: Build succeeds.

Then use the project's `run` skill (or `dotnet run --project src/EfSchemaVisualizer.Web`) to launch the app, paste in a class with an enum property (with and without an explicit `HasConversion<string>()`), and confirm:
- An enum property with no `HasConversion` shows a muted "→ int (default)" (or the enum's real underlying type) hint on the collapsed row, and the expanded panel's "Stored as" field is empty with that same hint as its placeholder.
- Setting "Stored as" to `string` in the expanded panel updates the collapsed-row hint to "→ string" and adds `.HasConversion<string>()` to the downloaded/regenerated source.
- Clearing the field removes the `HasConversion` call from source and the hint reverts to the enum default.
- A property with a two-lambda `HasConversion` in the pasted source shows "custom conversion" read-only, with no editable input.

- [ ] **Step 5: Run the full Web test suite to confirm the markup-source `SafeEdit` coverage test still passes**

Run: `dotnet test tests/EfSchemaVisualizer.Web.Tests --filter "FullyQualifiedName~GestureHandlerSafeEdit"`
Expected: PASS — `CommitValueConversion`'s call is wrapped in `SafeEdit`, satisfying the existing markup-source scan with no changes needed to that test itself.

- [ ] **Step 6: Commit**

```bash
git add src/EfSchemaVisualizer.Web/Diagram/EntityNode.razor
git commit -m "Render editable value-conversion field and enum default-storage hint"
```

---

### Task 8: Full-suite verification and backlog update

**Files:**
- Modify: `docs/backlog.md`

- [ ] **Step 1: Run the entire test suite**

Run: `dotnet test`
Expected: PASS, 0 failures, across `EfSchemaVisualizer.Core.Tests` and `EfSchemaVisualizer.Web.Tests`.

- [ ] **Step 2: Update the backlog entry**

In `docs/backlog.md`, find the line (Priority 2 section):

```
- [ ] **`[found]` Value converters and enums:** `HasConversion` (all overloads),
      `HasConversion<string>()` on enum properties. Enum properties currently
      render as their bare CLR type with no indication of how they're stored.
```

Replace it with a `[x]`-checked entry summarizing what was built and what's out of scope, following the style of the other closed items in that file (e.g. the "SQL-shaped mapping" or "Owned & complex types" entries directly above it) — reference `docs/superpowers/specs/2026-07-29-value-converters-and-enums-design.md` for the design, name the new `IsEnumType`/`EnumUnderlyingClrType`/`ConversionProviderClrType`/`ConversionIsCustomLambda` fields, and note the documented non-goals (`ValueConverter` instance overloads, `ConverterMappingHints`, editing a lambda-form conversion).

- [ ] **Step 3: Commit**

```bash
git add docs/backlog.md
git commit -m "Mark value converters & enum storage backlog item done"
```
